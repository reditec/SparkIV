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
using Velopack;

namespace SparkIV.WinUI
{
  public static class Program
  {
    /// <summary>
    /// The installer and the updater start SparkIV with their own arguments (install, update, uninstall);
    /// Velopack handles those first and exits. Otherwise the app starts as XAML would start it.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
      VelopackApp.Build().Run();
      XamlGeneratedProgram.XamlGeneratedMain();
    }
  }
}
