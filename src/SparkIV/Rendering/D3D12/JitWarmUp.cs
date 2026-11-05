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
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SparkIV.WinUI.Rendering.D3D12
{
  /// <summary>
  /// The drawing code compiled ahead on another thread (below normal priority), so the first frame that draws a scene
  /// doesn't compile it on the render thread and its workers (#112: the first frame with a location took 50 ms, the
  /// workers 12 ms each, compiling). Every method of the given types and of their nested ones (lambdas, local
  /// functions); generic ones left to their first call.
  /// </summary>
  public static class JitWarmUp
  {
    public static void Start(params Type[] types)
    {
      var thread = new Thread(() =>
      {
        foreach (var type in types)
        {
          Prepare(type);
        }
      })
      {
        IsBackground = true,
        Priority = ThreadPriority.BelowNormal,
        Name = "SparkIV JIT warm-up",
      };
      thread.Start();
    }

    private static void Prepare(Type type)
    {
      const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
      foreach (var method in type.GetMethods(all))
      {
        if (method.IsAbstract || method.ContainsGenericParameters)
        {
          continue;
        }
        try
        {
          RuntimeHelpers.PrepareMethod(method.MethodHandle);
        }
        catch (Exception)
        {
          // Not preparable here (e.g. needs a generic context): compiled at its first call as before.
        }
      }
      foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
      {
        if (!nested.ContainsGenericParameters)
        {
          Prepare(nested);
        }
      }
    }
  }
}
