using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace SparkIV.WinUI.Model
{
  /// <summary>
  /// A node in the tree of a model's parts (drawable, level of detail, mesh) with a check box for whether it is
  /// shown. Checking a node checks everything below it; a node whose children differ is shown as undecided.
  /// </summary>
  public sealed class ModelPart : INotifyPropertyChanged
  {
    private bool? _isChecked;
    private bool _updating;

    public ModelPart(string name, string detail, string glyph)
    {
      Name = name;
      Detail = detail;
      Glyph = glyph;
    }

    public string Name { get; }
    public string Detail { get; }
    public string Glyph { get; }
    public ModelPart? Parent { get; private set; }
    public ObservableCollection<ModelPart> Children { get; } = new ObservableCollection<ModelPart>();

    /// <summary>For a mesh: its number in the scene; -1 for the nodes above.</summary>
    public int MeshId { get; init; } = -1;

    /// <summary>For a level of detail: 0 (high) to 3 (very low); -1 for other nodes.</summary>
    public int Lod { get; init; } = -1;

    /// <summary>
    /// Shows one level of detail in every drawable that is shown (a drawable without it shows its lowest),
    /// without telling anyone; the caller sends the change once.
    /// </summary>
    public void ShowLevelOfDetail(int lod)
    {
      var levels = Children.Where(c => c.Lod >= 0).ToList();
      if (levels.Count > 0)
      {
        // A drawable that is hidden altogether stays hidden.
        if (levels.All(l => l.IsChecked == false))
        {
          return;
        }
        // The level asked for; without it the next coarser one, else the coarsest there is.
        var shown = levels.FirstOrDefault(l => l.Lod == lod)
                    ?? levels.Where(l => l.Lod > lod).OrderBy(l => l.Lod).FirstOrDefault()
                    ?? levels.OrderBy(l => l.Lod).Last();
        foreach (var level in levels)
        {
          level.SetFromParent(level == shown);
        }
        UpdateFromChildrenQuietly();
        return;
      }
      foreach (var child in Children)
      {
        child.ShowLevelOfDetail(lod);
      }
      if (Children.Count > 0)
      {
        UpdateFromChildrenQuietly();
      }
    }

    private void UpdateFromChildrenQuietly()
    {
      _updating = true;
      IsChecked = Children.All(c => c.IsChecked == true) ? true : Children.All(c => c.IsChecked == false) ? false : null;
      _updating = false;
    }

    /// <summary>How many levels of detail there are to choose from: one more than the coarsest in use below.</summary>
    public int MaxLevelsOfDetail() =>
      Math.Max(Children.Where(c => c.Lod >= 0).Select(c => c.Lod + 1).DefaultIfEmpty(0).Max(),
               Children.Select(c => c.MaxLevelsOfDetail()).DefaultIfEmpty(0).Max());

    public event PropertyChangedEventHandler? PropertyChanged;

    public ModelPart Add(ModelPart child)
    {
      child.Parent = this;
      Children.Add(child);
      return child;
    }

    public bool? IsChecked
    {
      get => _isChecked;
      set
      {
        if (_isChecked == value)
        {
          return;
        }
        // The check box goes from undecided to checked; a click on a checked box unchecks.
        _isChecked = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));

        if (_updating)
        {
          return;
        }
        if (value.HasValue)
        {
          foreach (var child in Children)
          {
            child.SetFromParent(value.Value);
          }
        }
        Parent?.UpdateFromChildren();
        Root.CheckedChanged?.Invoke();
      }
    }

    private ModelPart Root => Parent == null ? this : Parent.Root;

    /// <summary>On the root of a tree: called when a box in it was changed by the user.</summary>
    public System.Action? CheckedChanged { get; set; }

    private void SetFromParent(bool value)
    {
      _updating = true;
      IsChecked = value;
      _updating = false;
      foreach (var child in Children)
      {
        child.SetFromParent(value);
      }
    }

    private void UpdateFromChildren()
    {
      bool? state = Children.All(c => c.IsChecked == true) ? true : Children.All(c => c.IsChecked == false) ? false : null;
      _updating = true;
      IsChecked = state;
      _updating = false;
      Parent?.UpdateFromChildren();
    }

    /// <summary>Sets the box without telling anyone, for building the tree.</summary>
    public void Initialize(bool value)
    {
      SetFromParent(value);
    }

    /// <summary>After the tree is built: the boxes of the nodes above follow their children.</summary>
    public void SettleParents()
    {
      foreach (var child in Children)
      {
        child.SettleParents();
      }
      if (Children.Count > 0)
      {
        // Through the property, so the check box shows it too.
        UpdateFromChildrenQuietly();
      }
    }

    /// <summary>The meshes that are checked, in this node and below.</summary>
    public IEnumerable<int> CheckedMeshes()
    {
      if (MeshId >= 0)
      {
        if (IsChecked == true)
        {
          yield return MeshId;
        }
        yield break;
      }
      foreach (var mesh in Children.SelectMany(c => c.CheckedMeshes()))
      {
        yield return mesh;
      }
    }
  }
}
