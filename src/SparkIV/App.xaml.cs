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
using Microsoft.UI.Xaml;
using SparkIV.WinUI.Services;

namespace SparkIV.WinUI
{
  public partial class App : Application
  {
    private Window? _window;

    public App()
    {
      InitializeComponent();    }

    /// <summary>
    /// Started with a file ("Open with" in Explorer): an archive opens in the main window, any other file
    /// in its viewer alone; without one the main window opens empty. When the file isn't in a game folder
    /// and needs its game (archives, web pages), the game is asked for first.
    /// </summary>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
      // Started with administrator rights only to put saved files into a protected folder: no window.
      string[] arguments = Environment.GetCommandLineArgs();
      if (arguments.Length == 3 && arguments[1] == FileInstaller.HelperArgument)
      {
        Environment.Exit(FileInstaller.RunHelper(arguments[2]));
        return;
      }

      string? path = FileLaunch.FileFromCommandLine();
      if (path == null)
      {
        OpenMainWindow();
        return;
      }

      // While asking, the question's window closes before the next one opens; SparkIV must not end then.
      DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
      try
      {
        bool archive = FileLaunch.IsArchive(path);
        GameInstall? install = Games.ForFile(path);
        if (install == null && Games.NeedsGame(path))
        {
          // A web page also opens without its game; an archive can't be read without its key.
          install = await GameChooserWindow.AskAsync(path, optional: !archive);
          if (install == null && archive)
          {
            Exit();
            return;
          }
        }

        if (!archive)
        {
          try
          {
            FileLaunch.OpenInViewer(path, install);
            return;
          }
          catch (Exception)
          {
            // Then the main window opens instead.
          }
        }

        var main = OpenMainWindow();
        if (archive)
        {
          await main.OpenArchiveAsync(path, install);
        }
      }
      finally
      {
        DispatcherShutdownMode = DispatcherShutdownMode.OnLastWindowClose;
      }
    }

    private MainWindow OpenMainWindow()
    {
      var main = new MainWindow();
      _window = main;
      main.Activate();
      return main;
    }
  }
}
