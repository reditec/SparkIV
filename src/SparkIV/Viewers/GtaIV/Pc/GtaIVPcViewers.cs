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

using System.Collections.Generic;

namespace SparkIV.WinUI.Viewers.GtaIV.Pc
{
  /// <summary>
  /// The viewers written for GTA IV (PC) and the Episodes from Liberty City (the same engine version): what they
  /// read and how they draw it follows that version's files and GTAIV.exe, not RAGE in general.
  /// </summary>
  internal static class GtaIVPcViewers
  {
    /// <summary>The model view's GTA IV (PC) part (gtaiv-pc.js, embedded): see ModelViewPage.</summary>
    public const string PageScript = "SparkIV.GtaIV.Pc.gtaiv-pc.js";

    public static void Register(IDictionary<string, IViewer> byExtension)
    {
      byExtension[".cut"] = new CutsceneViewer();
      var collision = new CollisionViewer();
      byExtension[".wbn"] = collision;
      byExtension[".wbd"] = collision;
    }
  }
}
