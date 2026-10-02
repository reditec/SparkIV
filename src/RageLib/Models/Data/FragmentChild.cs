/**********************************************************************\

 RageLib - Models

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

namespace RageLib.Models.Data
{
  /// <summary>
  /// A part of a fragment that can break off (door, bonnet, wheel ...). It belongs to a bone of the skeleton
  /// of the fragment's drawable; most are drawn as part of that drawable and have no model of their own.
  /// </summary>
  public class FragmentChild
  {
    internal FragmentChild(int groupIndex, int boneIndex, float pristineMass, float damagedMass, float[] matrix1, float[] matrix2, Drawable drawable)
    {
      GroupIndex = groupIndex;
      BoneIndex = boneIndex;
      PristineMass = pristineMass;
      DamagedMass = damagedMass;
      Matrix1 = matrix1;
      Matrix2 = matrix2;
      Drawable = drawable;
    }

    public int GroupIndex { get; private set; }

    /// <summary>The index of the bone in the skeleton of the fragment's drawable (see <see cref="Bone.Index"/>).</summary>
    public int BoneIndex { get; private set; }

    public float PristineMass { get; private set; }
    public float DamagedMass { get; private set; }

    /// <summary>
    /// A 4x4 matrix, row by row with the translation in the last row; its translation is the negated position
    /// of the bone. What the second one stands for is not known yet.
    /// </summary>
    public float[] Matrix1 { get; private set; }
    public float[] Matrix2 { get; private set; }

    /// <summary>The child's own model, made around its bone; null for most children.</summary>
    public Drawable Drawable { get; private set; }
  }
}
