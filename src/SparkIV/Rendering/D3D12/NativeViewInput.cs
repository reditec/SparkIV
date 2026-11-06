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
using System.Linq;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace SparkIV.WinUI.Rendering.D3D12
{
  /// <summary>
  /// The native view's mouse and keys (#112 step 6), as the page's (ModelView.html "Mouse" ~4062-4110 and "Keys"
  /// ~4112-4146) and as the cutscene view takes them: left drag turns, middle or right drag (or Shift + left) moves, the
  /// wheel zooms, a double-click fits; W/S/A/D/Q/E walk while the view has the focus (a press in the panel gives it),
  /// Shift faster. A mouse drag's moves are read by the render thread from the cursor (CursorDrag); touch and pen by their
  /// events. For the model and animation views (the cutscene view keeps its own, which also leaves the film camera).
  /// </summary>
  public sealed class NativeViewInput : IDisposable
  {
    private readonly UserControl _view;
    private readonly SwapChainPanel _panel;
    private readonly ModelScenePlayer _player;
    private readonly Window _window;
    private uint? _dragPointer;
    private Windows.Foundation.Point _dragAt;
    private int _dragButton;
    private bool _dragMouse;
    private readonly HashSet<Windows.System.VirtualKey> _held = new HashSet<Windows.System.VirtualKey>();
    private bool _shiftDown;

    public NativeViewInput(UserControl view, SwapChainPanel panel, ModelScenePlayer player, Window window)
    {
      _view = view;
      _panel = panel;
      _player = player;
      _window = window;
      // The panel can't take the focus; the view itself does (its keys only while it has it, as the page's while focused).
      view.IsTabStop = true;
      panel.PointerPressed += Panel_PointerPressed;
      panel.PointerMoved += Panel_PointerMoved;
      panel.PointerReleased += Panel_PointerEnded;
      panel.PointerCanceled += Panel_PointerEnded;
      panel.PointerCaptureLost += Panel_PointerEnded;
      panel.PointerWheelChanged += Panel_PointerWheelChanged;
      panel.DoubleTapped += Panel_DoubleTapped;
      view.KeyDown += View_KeyDown;
      view.KeyUp += View_KeyUp;
      view.LostFocus += View_LostFocus;
      window.Activated += Window_Activated;
    }

    private void Panel_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
      var point = e.GetCurrentPoint(_panel);
      var kind = point.Properties.PointerUpdateKind;
      _dragButton = kind == PointerUpdateKind.MiddleButtonPressed ? 1 : kind == PointerUpdateKind.RightButtonPressed ? 2 : 0;
      _dragAt = point.Position;
      _dragPointer = e.Pointer.PointerId;
      _dragMouse = e.Pointer.PointerDeviceType == PointerDeviceType.Mouse;
      if (_dragMouse)
      {
        _player.BeginDrag(_dragButton, _view.XamlRoot?.RasterizationScale ?? 1, (float)_panel.ActualHeight);
      }
      _panel.CapturePointer(e.Pointer);
      _view.Focus(FocusState.Pointer);
      e.Handled = true;
    }

    private void Panel_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
      if (_dragPointer != e.Pointer.PointerId)
      {
        return;
      }
      if (_dragMouse)
      {
        // (Taken by the render thread from the cursor.)
        e.Handled = true;
        return;
      }
      var at = e.GetCurrentPoint(_panel).Position;
      float dx = (float)(at.X - _dragAt.X), dy = (float)(at.Y - _dragAt.Y);
      _dragAt = at;
      if (_dragButton == 0 && (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) == 0)
      {
        _player.Turn(dx, dy);
      }
      else
      {
        _player.Pan(dx, dy, (float)_panel.ActualHeight);
      }
      e.Handled = true;
    }

    private void Panel_PointerEnded(object sender, PointerRoutedEventArgs e)
    {
      if (_dragPointer == e.Pointer.PointerId)
      {
        _dragPointer = null;
        if (_dragMouse)
        {
          _player.EndDrag();
        }
        _panel.ReleasePointerCapture(e.Pointer);
      }
    }

    private void Panel_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
      var properties = e.GetCurrentPoint(_panel).Properties;
      if (properties.IsHorizontalMouseWheel || properties.MouseWheelDelta == 0)
      {
        return;
      }
      _player.Wheel(properties.MouseWheelDelta);
      e.Handled = true;
    }

    private void Panel_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
      _player.Fit();
      e.Handled = true;
    }

    private static bool KeyIsDown(Windows.System.VirtualKey key) =>
      (InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private void View_KeyDown(object sender, KeyRoutedEventArgs e)
    {
      if (!ReferenceEquals(FocusManager.GetFocusedElement(_view.XamlRoot), _view))
      {
        return;
      }
      _shiftDown = KeyIsDown(Windows.System.VirtualKey.Shift);
      bool combined = KeyIsDown(Windows.System.VirtualKey.Control) || KeyIsDown(Windows.System.VirtualKey.Menu) ||
                      KeyIsDown(Windows.System.VirtualKey.LeftWindows) || KeyIsDown(Windows.System.VirtualKey.RightWindows);
      if (combined || OrbitCamera.WalkKey((int)e.Key) == null)
      {
        SendWalk();
        return;
      }
      e.Handled = true;
      _held.Add(e.Key);
      SendWalk();
    }

    private void View_KeyUp(object sender, KeyRoutedEventArgs e)
    {
      _shiftDown = KeyIsDown(Windows.System.VirtualKey.Shift);
      _held.Remove(e.Key);
      SendWalk();
    }

    private void View_LostFocus(object sender, RoutedEventArgs e)
    {
      if (ReferenceEquals(e.OriginalSource, _view))
      {
        ClearWalk();
      }
    }

    private void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
      if (args.WindowActivationState == WindowActivationState.Deactivated)
      {
        ClearWalk();
      }
    }

    private void ClearWalk()
    {
      _held.Clear();
      _shiftDown = false;
      SendWalk();
    }

    private void SendWalk() => _player.Walk(_held.Select(k => OrbitCamera.WalkKey((int)k)!.Value), _shiftDown);

    public void Dispose()
    {
      _panel.PointerPressed -= Panel_PointerPressed;
      _panel.PointerMoved -= Panel_PointerMoved;
      _panel.PointerReleased -= Panel_PointerEnded;
      _panel.PointerCanceled -= Panel_PointerEnded;
      _panel.PointerCaptureLost -= Panel_PointerEnded;
      _panel.PointerWheelChanged -= Panel_PointerWheelChanged;
      _panel.DoubleTapped -= Panel_DoubleTapped;
      _view.KeyDown -= View_KeyDown;
      _view.KeyUp -= View_KeyUp;
      _view.LostFocus -= View_LostFocus;
      _window.Activated -= Window_Activated;
    }
  }
}
