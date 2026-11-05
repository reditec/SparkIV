/**********************************************************************\

 SparkIV
 Copyright (C) 2026  reditec

 This program is free software: you can redistribute it and/or modify
 it under the terms of the GNU General Public License as published by
 the Free Software Foundation, either version 3 of the License, or
 (at your option) any later version.

 This program is distributed in the hope that it will be useful,
 but WITHOUT ANY WARRANTY; without even the implied warranty of
 MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 GNU General Public License for more details.

 You should have received a copy of the GNU General Public License
 along with this program.  If not, see <http://www.gnu.org/licenses/>.

\**********************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Vortice.Direct3D12;

namespace SparkIV.WinUI.Rendering.D3D12
{
  /// <summary>
  /// Where a frame's time goes (#112): GPU timestamps round each pass (<see cref="Begin"/> / <see cref="End"/> by name,
  /// nested and repeated names allowed: a name's spans in a frame are summed), the CPU's time between the same marks
  /// (the recording), the draw calls a pass adds (when the caller passes its counter), and CPU stages of the render
  /// thread (<see cref="Cpu"/>). The timestamps of a frame are resolved into a readback buffer at <see cref="EndFrame"/>
  /// and read once the GPU has passed the frame's fence mark (<see cref="FrameSent"/>), a frame or two later, never
  /// waited for. Made only when profiling is on (<see cref="D3D12Device.Profiler"/>): when it is null nothing is recorded.
  /// </summary>
  public sealed class GpuProfiler : IDisposable
  {
    /// <summary>In the app: SPARKIV_D3D12_PROFILE=1 (a block a second into SparkIV-d3d12.log).</summary>
    public static bool EnabledInApp { get; } = Environment.GetEnvironmentVariable("SPARKIV_D3D12_PROFILE") == "1";

    private const int Slots = 8;
    private const int QueriesPerSlot = 512;
    private const string FrameName = "frame (GPU total)";

    private readonly D3D12Device _device;
    private readonly ID3D12QueryHeap _heap;
    private readonly ID3D12Resource _readback;
    private readonly unsafe ulong* _times;
    private readonly double _ticksToMs;
    private readonly Slot[] _slots = new Slot[Slots];
    private int _current = -1;
    private long _frameCount;

    private readonly List<string> _names = new List<string>();
    private readonly Dictionary<string, int> _nameIndex = new Dictionary<string, int>();
    private readonly List<string> _cpuNames = new List<string>();
    private readonly Dictionary<string, int> _cpuIndex = new Dictionary<string, int>();
    private double[] _cpuFrame = new double[16];

    // Per pass name: per collected GPU frame its GPU ms, CPU recording ms, draws (only frames where it ran).
    private readonly List<Series> _gpu = new List<Series>();
    private readonly List<Series> _rec = new List<Series>();
    private readonly List<Series> _draws = new List<Series>();
    private readonly List<Series> _cpu = new List<Series>();

    /// <summary>GPU frames collected and CPU frames counted since the last <see cref="Reset"/>; frames not recorded (slot busy).</summary>
    public int GpuFrames { get; private set; }
    public int CpuFrames { get; private set; }
    public int Skipped { get; private set; }

    private sealed class Slot
    {
      public ulong Mark;
      public bool Sent;
      public int Queries;
      public readonly List<MarkRecord> Marks = new List<MarkRecord>();
    }

    private struct MarkRecord
    {
      public int Name;
      public int BeginQuery;
      public int EndQuery;
      public long CpuStart;
      public double CpuMs;
      public int DrawsStart;
      public int Draws;
    }

    private sealed class Series
    {
      public readonly List<double> Values = new List<double>();
      public double Sum;
      public double Max;

      public void Add(double v)
      {
        Values.Add(v);
        Sum += v;
        Max = Math.Max(Max, v);
      }

      public void Clear()
      {
        Values.Clear();
        Sum = Max = 0;
      }

      // Over all frames (those without the pass as 0).
      public double Mean(int frames) => frames > 0 ? Sum / frames : 0;

      public double Percentile(int frames, double q)
      {
        if (frames <= 0)
        {
          return 0;
        }
        var sorted = new double[Math.Max(frames, Values.Count)];
        Values.CopyTo(sorted, sorted.Length - Values.Count);
        Array.Sort(sorted);
        return sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
      }
    }

    public unsafe GpuProfiler(D3D12Device device)
    {
      _device = device;
      var d3d = device.Device;
      _heap = d3d.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription(QueryHeapType.Timestamp, Slots * QueriesPerSlot));
      _readback = d3d.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None,
        ResourceDescription.Buffer((ulong)Slots * QueriesPerSlot * 8), ResourceStates.CopyDest);
      void* mapped;
      _readback.Map(0, null, &mapped).CheckError();
      _times = (ulong*)mapped;
      device.Queue.GetTimestampFrequency(out ulong frequency).CheckError();
      _ticksToMs = 1000.0 / frequency;
      for (int i = 0; i < Slots; i++)
      {
        _slots[i] = new Slot();
      }
    }

    /// <summary>The CPU's clock now (for <see cref="CpuSince"/>).</summary>
    public static long Now => Stopwatch.GetTimestamp();

    private Slot? Recording => _current >= 0 ? _slots[_current] : null;

    /// <summary>
    /// The frame's first mark (after the command list is opened): frames the GPU has finished are read first; the frame
    /// is recorded only when its slot is free (else skipped, nothing waited for).
    /// </summary>
    public void BeginFrame(ID3D12GraphicsCommandList list)
    {
      Collect();
      var slot = _slots[(int)(_frameCount % Slots)];
      if (slot.Sent)
      {
        _current = -1;
        Skipped++;
        return;
      }
      _current = (int)(_frameCount % Slots);
      slot.Queries = 0;
      slot.Marks.Clear();
      Begin(list, FrameName);
    }

    /// <summary>The frame's last mark (before the command list is closed): its timestamps resolved into the readback buffer.</summary>
    public void EndFrame(ID3D12GraphicsCommandList list)
    {
      if (Recording is not Slot slot)
      {
        return;
      }
      End(list, FrameName);
      if (slot.Queries > 0)
      {
        list.ResolveQueryData(_heap, QueryType.Timestamp, (uint)(_current * QueriesPerSlot), (uint)slot.Queries,
                              _readback, (ulong)_current * QueriesPerSlot * 8);
      }
    }

    /// <summary>After the frame's list is sent: the fence mark it is done at; the frame's CPU stages counted.</summary>
    public void FrameSent(ulong mark)
    {
      if (Recording is Slot slot)
      {
        slot.Mark = mark;
        slot.Sent = true;
      }
      _current = -1;
      _frameCount++;
      for (int i = 0; i < _cpuNames.Count; i++)
      {
        _cpu[i].Add(_cpuFrame[i]);
        _cpuFrame[i] = 0;
      }
      CpuFrames++;
    }

    /// <summary>A pass begins (its GPU timestamp, the CPU's clock; <paramref name="draws"/>: the caller's draw counter now, or -1).</summary>
    public void Begin(ID3D12GraphicsCommandList list, string name, int draws = -1) => Open(list, name, draws);

    /// <summary>The pass of that name begun last ends (<paramref name="draws"/>: the same counter now, or -1).</summary>
    public void End(ID3D12GraphicsCommandList list, string name, int draws = -1)
    {
      lock (_marksLock)
      {
        if (Recording is not Slot slot || !_nameIndex.TryGetValue(name, out int n))
        {
          return;
        }
        for (int i = slot.Marks.Count - 1; i >= 0; i--)
        {
          var m = slot.Marks[i];
          if (m.Name == n && m.EndQuery < 0)
          {
            CloseMark(list, slot, i, draws);
            return;
          }
        }
      }
    }

    /// <summary>
    /// A pass begins (as <see cref="Begin"/>), from any thread into any of the frame's command lists (lists recorded on
    /// worker threads); the mark's number for <see cref="Close"/> (-1: none).
    /// </summary>
    public int Open(ID3D12GraphicsCommandList list, string name, int draws = -1)
    {
      lock (_marksLock)
      {
        if (Recording is not Slot slot || slot.Queries + 2 > QueriesPerSlot)
        {
          return -1;
        }
        int query = slot.Queries++;
        list.EndQuery(_heap, QueryType.Timestamp, (uint)(_current * QueriesPerSlot + query));
        slot.Marks.Add(new MarkRecord { Name = NameOf(name), BeginQuery = query, EndQuery = -1, CpuStart = Now, DrawsStart = draws, Draws = -1 });
        return slot.Marks.Count - 1;
      }
    }

    /// <summary>The pass of <see cref="Open"/>'s number ends (any thread, the list it began in).</summary>
    public void Close(ID3D12GraphicsCommandList list, int mark, int draws = -1)
    {
      lock (_marksLock)
      {
        if (mark >= 0 && Recording is Slot slot && mark < slot.Marks.Count && slot.Marks[mark].EndQuery < 0)
        {
          CloseMark(list, slot, mark, draws);
        }
      }
    }

    private readonly object _marksLock = new object();

    private void CloseMark(ID3D12GraphicsCommandList list, Slot slot, int i, int draws)
    {
      if (slot.Queries + 1 > QueriesPerSlot)
      {
        return;
      }
      var m = slot.Marks[i];
      int query = slot.Queries++;
      list.EndQuery(_heap, QueryType.Timestamp, (uint)(_current * QueriesPerSlot + query));
      m.EndQuery = query;
      m.CpuMs = (Now - m.CpuStart) * 1000.0 / Stopwatch.Frequency;
      m.Draws = draws >= 0 && m.DrawsStart >= 0 ? Math.Max(0, draws - m.DrawsStart) : -1;
      slot.Marks[i] = m;
    }

    /// <summary>A CPU stage of the frame on the render thread (summed per frame, counted at <see cref="FrameSent"/>).</summary>
    public void Cpu(string name, double ms) => Add(name, ms, false);

    /// <summary>A number of the frame (draw calls, instances...), summed per frame like a CPU stage, shown without "ms".</summary>
    public void Count(string name, double value) => Add(name, value, true);

    private readonly List<bool> _isCount = new List<bool>();

    private void Add(string name, double ms, bool count)
    {
      if (!_cpuIndex.TryGetValue(name, out int i))
      {
        i = _cpuNames.Count;
        _cpuIndex[name] = i;
        _cpuNames.Add(name);
        _isCount.Add(count);
        _cpu.Add(new Series());
        if (_cpuFrame.Length <= i)
        {
          Array.Resize(ref _cpuFrame, _cpuFrame.Length * 2);
        }
        // (Earlier frames had it at 0.)
        for (int k = 0; k < CpuFrames; k++)
        {
          _cpu[i].Values.Add(0);
        }
      }
      _cpuFrame[i] += ms;
    }

    /// <summary>A CPU stage from <paramref name="start"/> (<see cref="Now"/>) until now.</summary>
    public void CpuSince(string name, long start) => Cpu(name, (Now - start) * 1000.0 / Stopwatch.Frequency);

    private int NameOf(string name)
    {
      if (!_nameIndex.TryGetValue(name, out int n))
      {
        n = _names.Count;
        _nameIndex[name] = n;
        _names.Add(name);
        _gpu.Add(new Series());
        _rec.Add(new Series());
        _draws.Add(new Series());
      }
      return n;
    }

    /// <summary>The frames the GPU has finished read (never waits).</summary>
    public unsafe void Collect()
    {
      for (int s = 0; s < Slots; s++)
      {
        var slot = _slots[s];
        if (!slot.Sent || !_device.IsDone(slot.Mark))
        {
          continue;
        }
        var gpu = new double[_names.Count];
        var rec = new double[_names.Count];
        var draws = new int[_names.Count];
        var seen = new bool[_names.Count];
        var drawn = new bool[_names.Count];
        ulong* t = _times + (long)s * QueriesPerSlot;
        foreach (var m in slot.Marks)
        {
          if (m.EndQuery < 0)
          {
            continue;
          }
          ulong a = t[m.BeginQuery], b = t[m.EndQuery];
          gpu[m.Name] += b > a ? (b - a) * _ticksToMs : 0;
          rec[m.Name] += m.CpuMs;
          seen[m.Name] = true;
          if (m.Draws >= 0)
          {
            draws[m.Name] += m.Draws;
            drawn[m.Name] = true;
          }
        }
        for (int n = 0; n < _names.Count; n++)
        {
          if (seen[n])
          {
            _gpu[n].Add(gpu[n]);
            _rec[n].Add(rec[n]);
          }
          if (drawn[n])
          {
            _draws[n].Add(draws[n]);
          }
        }
        GpuFrames++;
        slot.Sent = false;
        slot.Mark = 0;
      }
    }

    /// <summary>Waits for the GPU and reads every frame sent (at the end of a bench).</summary>
    public void Flush()
    {
      _device.WaitIdle();
      Collect();
    }

    /// <summary>The figures gathered so far forgotten (the names kept).</summary>
    public void Reset()
    {
      foreach (var list in new[] { _gpu, _rec, _draws, _cpu })
      {
        foreach (var s in list)
        {
          s.Clear();
        }
      }
      GpuFrames = CpuFrames = Skipped = 0;
    }

    /// <summary>
    /// A table: per pass the GPU ms (mean over all frames, then the max or the 95th percentile), the CPU's recording ms
    /// between its marks and its draw calls a frame; then the CPU stages (mean and max or p95).
    /// </summary>
    public string Report(bool p95)
    {
      var inv = CultureInfo.InvariantCulture;
      string spread = p95 ? "p95" : "max";
      var text = new StringBuilder();
      text.AppendLine(string.Format(inv, "profile: {0} GPU frames, {1} CPU frames{2}", GpuFrames, CpuFrames, Skipped > 0 ? ", " + Skipped + " not recorded" : ""));
      text.AppendLine(string.Format(inv, "  {0,-30} {1,9} {2,9} {3,9} {4,8}", "pass", "GPU ms", spread, "CPU rec", "draws"));
      for (int n = 0; n < _names.Count; n++)
      {
        if (_gpu[n].Values.Count == 0)
        {
          continue;
        }
        text.AppendLine(string.Format(inv, "  {0,-30} {1,9:0.000} {2,9:0.000} {3,9:0.000} {4,8}", _names[n],
          _gpu[n].Mean(GpuFrames), p95 ? _gpu[n].Percentile(GpuFrames, 0.95) : _gpu[n].Max, _rec[n].Mean(GpuFrames),
          _draws[n].Values.Count > 0 ? _draws[n].Mean(GpuFrames).ToString("0", inv) : "-"));
      }
      foreach (bool counts in new[] { false, true })
      {
        text.AppendLine(string.Format(inv, "  {0,-30} {1,9} {2,9}", counts ? "count a frame" : "CPU stage", counts ? "mean" : "ms", spread));
        for (int i = 0; i < _cpuNames.Count; i++)
        {
          if (_isCount[i] != counts)
          {
            continue;
          }
          string format = counts ? "  {0,-30} {1,9:0.##} {2,9:0.##}" : "  {0,-30} {1,9:0.000} {2,9:0.000}";
          text.AppendLine(string.Format(inv, format, _cpuNames[i], _cpu[i].Mean(CpuFrames),
            p95 ? _cpu[i].Percentile(CpuFrames, 0.95) : _cpu[i].Max));
        }
      }
      return text.ToString().TrimEnd();
    }

    public void Dispose()
    {
      _readback.Unmap(0);
      _readback.Dispose();
      _heap.Dispose();
    }
  }
}
