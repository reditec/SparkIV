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
using System.Linq;
using Microsoft.UI.Xaml;
using File = RageLib.FileSystem.Common.File;

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
{
  /// <summary>
  /// GTA IV (PC) animation dictionaries (.wad): one that is a cutscene's section (cuts.img: a .cut beside it whose
  /// [ANIM] names it, bc_1_0.wad → bc_1.cut's first section) opens in the cutscene view with that section alone - its
  /// clips need the .cut's models, variations, props, place, sound and subtitles; any other in the animation view (the
  /// game's moves, played on a protagonist).
  /// </summary>
  internal sealed class CutsceneWadViewer : IViewer
  {
    private readonly IViewer _animations;
    private readonly CutsceneViewer _cutscenes = new CutsceneViewer();

    public CutsceneWadViewer(IViewer animations)
    {
      _animations = animations;
    }

    public object Load(File file, byte[] data)
    {
      if (SectionOf(file) is (File cut, int section))
      {
        try
        {
          var document = (CutsceneDocument)_cutscenes.Load(cut, cut.GetData());
          document.OnlySection = section;
          return document;
        }
        catch (Exception)
        {
          // Then as a plain animation dictionary.
        }
      }
      return _animations.Load(file, data);
    }

    public FrameworkElement CreateView(ViewerContext context, object loaded) =>
      loaded is CutsceneDocument ? _cutscenes.CreateView(context, loaded) : _animations.CreateView(context, loaded);

    /// <summary>The .cut beside the dictionary with a section that plays it, and that section's number; null if none.</summary>
    private static (File Cut, int Section)? SectionOf(File wad)
    {
      string name = System.IO.Path.GetFileNameWithoutExtension(wad.Name);
      if (wad.ParentDirectory is not RageLib.FileSystem.Common.Directory folder)
      {
        return null;
      }
      // bc_1_0 → bc_1.cut first (the usual naming), then every .cut of the folder.
      int underscore = name.LastIndexOf('_');
      var likely = underscore > 0 ? folder.FindByName(name[..underscore] + ".cut") as File : null;
      foreach (var cut in new[] { likely }.Concat(folder.OfType<File>().Where(f => f.Name.EndsWith(".cut", StringComparison.OrdinalIgnoreCase))))
      {
        if (cut == null)
        {
          continue;
        }
        try
        {
          var parsed = CutFile.Parse(System.Text.Encoding.ASCII.GetString(cut.GetData()));
          int section = parsed.Sections.FindIndex(s => string.Equals(s.Anim, name, StringComparison.OrdinalIgnoreCase));
          if (section >= 0)
          {
            return (cut, section);
          }
        }
        catch (Exception)
        {
          // Not a .cut that can be read: the next.
        }
      }
      return null;
    }
  }
}
