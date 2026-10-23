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

using System.IO;
using System.Text;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>
  /// The 3D view's page (ModelView.html) as the views serve it: the page with the game's own part (how that game
  /// and version draws: its shaders' formulas, its water, its lamps) put in before the page's script.
  /// </summary>
  internal static class ModelViewPage
  {
    public static byte[] Bytes()
    {
      string page = Resource("SparkIV.ModelView.html");
      string game = Resource(GameScript());
      int at = page.IndexOf("<script>");
      return Encoding.UTF8.GetBytes(page.Insert(at, "<script>\n" + game + "\n</script>\n"));
    }

    // The game's part: GTA IV (PC), with its episodes, is the only game so far.
    private static string GameScript() => GtaIV.Pc.GtaIVPcViewers.PageScript;

    private static string Resource(string name)
    {
      using var stream = typeof(ModelViewPage).Assembly.GetManifestResourceStream(name)!;
      using var reader = new StreamReader(stream, Encoding.UTF8);
      return reader.ReadToEnd();
    }
  }
}
