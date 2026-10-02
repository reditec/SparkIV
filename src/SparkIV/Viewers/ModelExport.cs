using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SparkIV.WinUI.Viewers
{
  /// <summary>
  /// Writes what the model viewer shows (one level of detail, the parts ticked, wheels in their places)
  /// into a file for other programs, with the textures as PNG files next to it:
  /// Valve Studiomdl (.smd, with the skeleton, as the old SparkIV wrote) or Wavefront (.obj with .mtl,
  /// which Blender, 3ds Max and others open). The old SparkIV wrote every level of detail on top of
  /// each other and the wheels at the origin.
  /// </summary>
  internal static class ModelExport
  {
    /// <summary>Returns the number of texture files written next to it.</summary>
    public static int Write(ModelScene scene, IReadOnlyCollection<int> meshIds, string path)
    {
      var meshes = meshIds.Select(id => scene.Meshes[id]).ToList();
      string folder = Path.GetDirectoryName(path)!;
      var textureFiles = WriteTextures(scene, meshes, folder);
      if (Path.GetExtension(path).Equals(".obj", StringComparison.OrdinalIgnoreCase))
      {
        WriteObj(scene, meshes, path, textureFiles);
      }
      else
      {
        WriteSmd(scene, meshes, path, textureFiles);
      }
      return textureFiles.Count;
    }

    private static string F(float f) => f.ToString("0.000000", CultureInfo.InvariantCulture);

    /// <summary>The textures the meshes use as PNG files ("admiral_badges.png"), by material.</summary>
    private static Dictionary<int, string> WriteTextures(ModelScene scene, List<SceneMesh> meshes, string folder)
    {
      var files = new Dictionary<int, string>();
      foreach (int id in meshes.Select(m => m.Material).Where(m => m >= 0).Distinct())
      {
        var material = scene.Materials[id];
        if (material.TextureKey == null || material.TextureName == null || !scene.Textures.TryGetValue(material.TextureKey, out var png))
        {
          continue;
        }
        string name = FileName(material.TextureName) + ".png";
        System.IO.File.WriteAllBytes(Path.Combine(folder, name), png);
        files[id] = name;
      }
      return files;
    }

    /// <summary>A texture name as a file name: without "pack:/", folders and extension.</summary>
    private static string FileName(string textureName)
    {
      string name = textureName.Replace('\\', '/');
      name = name.Substring(name.LastIndexOf('/') + 1);
      name = Path.GetFileNameWithoutExtension(name);
      foreach (char c in Path.GetInvalidFileNameChars())
      {
        name = name.Replace(c, '_');
      }
      return name.Length > 0 ? name : "texture";
    }

    private static string MaterialName(ModelScene scene, int material, Dictionary<int, string> textures) =>
      textures.TryGetValue(material, out var file) ? file
      : material >= 0 ? "material_" + material + "_" + scene.Materials[material].Shader
      : "material";

    private static void WriteSmd(ModelScene scene, List<SceneMesh> meshes, string path, Dictionary<int, string> textures)
    {
      var sb = new StringBuilder();
      sb.AppendLine("version 1");
      sb.AppendLine("nodes");
      if (scene.Bones.Count == 0)
      {
        sb.AppendLine("0 \"root\" -1");
      }
      foreach (var bone in scene.Bones)
      {
        sb.AppendLine(bone.Index + " \"" + bone.Name + "\" " + bone.Parent);
      }
      sb.AppendLine("end");
      sb.AppendLine("skeleton");
      sb.AppendLine("time 0");
      if (scene.Bones.Count == 0)
      {
        sb.AppendLine("0   0.000000 0.000000 0.000000   0.000000 0.000000 0.000000");
      }
      foreach (var bone in scene.Bones)
      {
        sb.AppendLine(bone.Index + "   " + F(bone.X) + " " + F(bone.Y) + " " + F(bone.Z) + "   " +
                      F(bone.RotationX) + " " + F(bone.RotationY) + " " + F(bone.RotationZ));
      }
      sb.AppendLine("end");
      sb.AppendLine("triangles");
      foreach (var mesh in meshes)
      {
        string material = MaterialName(scene, mesh.Material, textures);
        for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
        {
          sb.AppendLine(material);
          for (int k = 0; k < 3; k++)
          {
            int v = mesh.Indices[t + k];
            float nx = 0, ny = 0, nz = 0, u = 0, w = 0;
            if (mesh.Normals != null)
            {
              nx = mesh.Normals[v * 3];
              ny = mesh.Normals[v * 3 + 1];
              nz = mesh.Normals[v * 3 + 2];
            }
            if (mesh.Uvs != null)
            {
              u = mesh.Uvs[v * 2];
              w = mesh.Uvs[v * 2 + 1];
            }
            sb.AppendLine(mesh.Bones[v] + "   " + F(mesh.Positions[v * 3]) + " " + F(mesh.Positions[v * 3 + 1]) + " " + F(mesh.Positions[v * 3 + 2]) + "   " +
                          F(nx) + " " + F(ny) + " " + F(nz) + "   " + F(u) + " " + F(w) + "   1 " + mesh.Bones[v] + " 1.000000");
          }
        }
      }
      sb.AppendLine("end");
      System.IO.File.WriteAllText(path, sb.ToString());
    }

    /// <summary>
    /// OBJ is y-up: the game's z-up turned so the model stands upright after importing (x, z, -y). Texture
    /// rows count from the bottom there (v turned round).
    /// </summary>
    private static void WriteObj(ModelScene scene, List<SceneMesh> meshes, string path, Dictionary<int, string> textures)
    {
      string mtl = Path.ChangeExtension(path, ".mtl");
      var obj = new StringBuilder();
      obj.AppendLine("# Exported by SparkIV");
      obj.AppendLine("mtllib " + Path.GetFileName(mtl));
      int vertexBase = 1;
      for (int n = 0; n < meshes.Count; n++)
      {
        var mesh = meshes[n];
        int count = mesh.Positions.Length / 3;
        obj.AppendLine("o mesh_" + (n + 1));
        for (int v = 0; v < count; v++)
        {
          obj.AppendLine("v " + F(mesh.Positions[v * 3]) + " " + F(mesh.Positions[v * 3 + 2]) + " " + F(-mesh.Positions[v * 3 + 1]));
        }
        if (mesh.Uvs != null)
        {
          for (int v = 0; v < count; v++)
          {
            obj.AppendLine("vt " + F(mesh.Uvs[v * 2]) + " " + F(1 - mesh.Uvs[v * 2 + 1]));
          }
        }
        if (mesh.Normals != null)
        {
          for (int v = 0; v < count; v++)
          {
            obj.AppendLine("vn " + F(mesh.Normals[v * 3]) + " " + F(mesh.Normals[v * 3 + 2]) + " " + F(-mesh.Normals[v * 3 + 1]));
          }
        }
        obj.AppendLine("usemtl " + ObjMaterial(mesh.Material));
        for (int t = 0; t + 2 < mesh.Indices.Length; t += 3)
        {
          obj.Append('f');
          for (int k = 0; k < 3; k++)
          {
            int i = mesh.Indices[t + k] + vertexBase;
            obj.Append(' ').Append(i);
            if (mesh.Uvs != null || mesh.Normals != null)
            {
              obj.Append('/').Append(mesh.Uvs != null ? i.ToString(CultureInfo.InvariantCulture) : "");
              if (mesh.Normals != null)
              {
                obj.Append('/').Append(i);
              }
            }
          }
          obj.AppendLine();
        }
        vertexBase += count;
      }
      System.IO.File.WriteAllText(path, obj.ToString());

      var materials = new StringBuilder();
      foreach (int id in meshes.Select(m => m.Material).Distinct())
      {
        materials.AppendLine("newmtl " + ObjMaterial(id));
        materials.AppendLine("Kd 1.000000 1.000000 1.000000");
        if (id >= 0 && scene.Materials[id].Glass)
        {
          materials.AppendLine("d 0.500000");
        }
        if (textures.TryGetValue(id, out var file))
        {
          materials.AppendLine("map_Kd " + file);
        }
        materials.AppendLine();
      }
      System.IO.File.WriteAllText(mtl, materials.ToString());
    }

    private static string ObjMaterial(int material) => material >= 0 ? "material_" + material : "material";
  }
}
