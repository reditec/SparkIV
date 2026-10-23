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
