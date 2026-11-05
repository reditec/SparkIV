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

using System.Numerics;

namespace SparkIV.WinUI.Rendering.D3D12
{
  /// <summary>
  /// What the native view draws, asked on its render thread once a frame: it brings the renderer up to date (models,
  /// instances, bones) and says through which camera and light the frame is seen.
  /// </summary>
  public interface IRenderScene
  {
    /// <summary>
    /// Before a frame (render thread): <paramref name="now"/> the view's clock in seconds, the picture's size in pixels.
    /// Returns the camera (view-projection, eye), the frame's light and how it becomes the picture, and whether it plays
    /// (the eye adapts gently then, at once when stopped).
    /// </summary>
    SceneView Update(MeshRenderer renderer, double now, int width, int height);

    /// <summary>
    /// Draws what lies behind the meshes into the light target (a sky; render thread, after the target is cleared,
    /// before the meshes). Nothing by default.
    /// </summary>
    void DrawBackground(D3D12Device device, Vortice.Direct3D12.ID3D12GraphicsCommandList list, Vortice.Direct3D12.CpuDescriptorHandle lightTarget,
                        int width, int height, SceneView view)
    {
    }

    /// <summary>
    /// Draws what goes between the solid meshes and the see-through ones (water; render thread, with its own states).
    /// <paramref name="depthBuffer"/>: the depth's resource (DEPTH_WRITE; e.g. to copy the solid scene's depth). Nothing
    /// by default.
    /// </summary>
    void DrawAfterSolid(D3D12Device device, Vortice.Direct3D12.ID3D12GraphicsCommandList list, Vortice.Direct3D12.CpuDescriptorHandle lightTarget,
                        Vortice.Direct3D12.CpuDescriptorHandle depth, int width, int height, SceneView view,
                        Vortice.Direct3D12.ID3D12Resource? depthBuffer)
    {
    }

    /// <summary>
    /// Draws the sky into the mirrored surroundings' map (its target, RENDER_TARGET, size x size; render thread, before
    /// the meshes): only when the view asks for the map (<see cref="SceneView.ParabolicHaze"/>). Nothing by default.
    /// </summary>
    void DrawParabolicSky(D3D12Device device, Vortice.Direct3D12.ID3D12GraphicsCommandList list, Vortice.Direct3D12.CpuDescriptorHandle target,
                          int size, SceneView view)
    {
    }
  }

  /// <summary>Through what and in what light a frame is seen (see <see cref="IRenderScene"/>).</summary>
  public readonly record struct SceneView(Matrix4x4 ViewProjection, Vector3 Eye, FrameLight Light, ToneParams Tone, bool Playing)
  {
    /// <summary>The lights added over the solid scene (lamps, a cutscene's own; see <see cref="DeferredLight"/>), or null.</summary>
    public System.Collections.Generic.IReadOnlyList<DeferredLight>? Lights { get; init; }

    /// <summary>The lamps' coronas added after the scene (see <see cref="Corona"/>), or null.</summary>
    public System.Collections.Generic.IReadOnlyList<Corona>? Coronas { get; init; }

    /// <summary>The lamps' visible light volumes added after the scene (see <see cref="LightVolume"/>), or null.</summary>
    public System.Collections.Generic.IReadOnlyList<LightVolume>? Volumes { get; init; }

    /// <summary>
    /// The mirrored surroundings' map wanted (outside): how far its objects are taken (the haze's end); null: no map.
    /// </summary>
    public float? ParabolicHaze { get; init; }

    /// <summary>The frame's depth of field and fade (#112 step 4h); none by default.</summary>
    public PostParams Post { get; init; }
  }

}
