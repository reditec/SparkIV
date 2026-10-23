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
