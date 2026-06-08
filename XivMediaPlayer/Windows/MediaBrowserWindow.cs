using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using MediaPlayerCore.Catalog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace XivMediaPlayer.Windows {
  internal class MediaBrowserWindow : Window {
    private readonly List<IMediaCatalogProvider> _providers = new List<IMediaCatalogProvider>();
    private int _selectedProviderIndex = 0;
    private MediaCatalog? _currentCatalog;
    private string _searchQuery = "";
    private string _selectedCategory = "";
    private string _statusMessage = "";
    private bool _isLoading;
    private List<MediaCatalogItem> _filteredItems = new List<MediaCatalogItem>();
    private int _forceSelectProviderIndex = -1;

    /// <summary>
    /// Fired when the user clicks Play on a catalog item.
    /// The string is the resolved stream URL.
    /// </summary>
    public event EventHandler<MediaCatalogItem>? OnPlayRequested;

    public MediaBrowserWindow() :
      base("Media Browser###MediaBrowser",
        ImGuiWindowFlags.NoCollapse,
        false) {
      Size = new Vector2(620, 480);
      SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void AddProvider(IMediaCatalogProvider provider) {
      _providers.Add(provider);
    }

    public void RemoveProvider(IMediaCatalogProvider provider) {
      _providers.Remove(provider);
    }

    public void SelectHistoryTab() {
      for (int i = 0; i < _providers.Count; i++) {
        if (_providers[i].Name == "History") {
          _forceSelectProviderIndex = i;
          break;
        }
      }
    }

    public override void Draw() {
      // Provider selector 
      if (_providers.Count == 0) {
        ImGui.TextColored(new Vector4(1f, 0.6f, 0.3f, 1f), "No media providers configured.");
        ImGui.TextWrapped("Add playlists to the playlists folder or configure a YouTube playlist URL in settings.");
        return;
      }

      // Provider tabs
      string[] providerNames = _providers.Select(p => p.Name).ToArray();
      if (ImGui.BeginTabBar("##ProviderTabs")) {
        for (int i = 0; i < _providers.Count; i++) {
          ImGuiTabItemFlags flags = ImGuiTabItemFlags.None;
          if (_forceSelectProviderIndex == i) {
            flags = ImGuiTabItemFlags.SetSelected;
            _forceSelectProviderIndex = -1;
          }
          
          bool open = true;
          if (ImGui.BeginTabItem(providerNames[i], ref open, flags)) {
            if (_selectedProviderIndex != i) {
              _selectedProviderIndex = i;
              _currentCatalog = null;
              _searchQuery = "";
              _selectedCategory = "";
              _filteredItems.Clear();
              LoadCatalog();
            }
            DrawCatalogView();
            ImGui.EndTabItem();
          }
        }
        ImGui.EndTabBar();
      }

      // Status bar
      if (!string.IsNullOrEmpty(_statusMessage)) {
        ImGui.Separator();
        ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), _statusMessage);
      }
    }

    private void DrawCatalogView() {
      var provider = _providers[_selectedProviderIndex];

      if (!provider.IsAvailable) {
        ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "This provider is not available.");
        ImGui.TextWrapped(provider.Description);
        return;
      }

      // Load catalog if not loaded
      if (_currentCatalog == null && !_isLoading) {
        LoadCatalog();
      }

      if (_isLoading) {
        ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "Loading catalog...");
        return;
      }

      if (_currentCatalog == null || _currentCatalog.Items.Count == 0) {
        ImGui.Text("No items found.");
        if (ImGui.Button("Refresh")) {
          RefreshCatalog();
        }
        return;
      }

      // Toolbar: Search + Refresh 
      float availWidth = ImGui.GetContentRegionAvail().X;

      ImGui.SetNextItemWidth(availWidth - 80);
      if (ImGui.InputTextWithHint("##Search", "Search...", ref _searchQuery, 256)) {
        ApplyFilter();
      }
      ImGui.SameLine();
      if (ImGui.Button("Refresh", new Vector2(72, 0))) {
        RefreshCatalog();
      }

      ImGui.Spacing();

      // Category filter 
      var categories = _currentCatalog.Categories.ToList();
      if (categories.Count > 1) {
        if (ImGui.BeginTabBar("##CategoryTabs")) {
          // "All" tab
          if (ImGui.BeginTabItem("All")) {
            if (_selectedCategory != "") {
              _selectedCategory = "";
              ApplyFilter();
            }
            ImGui.EndTabItem();
          }
          foreach (string cat in categories) {
            if (ImGui.BeginTabItem(cat)) {
              if (_selectedCategory != cat) {
                _selectedCategory = cat;
                ApplyFilter();
              }
              ImGui.EndTabItem();
            }
          }
          ImGui.EndTabBar();
        }
      }

      // Item list 
      ImGui.Spacing();
      ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f),
        $"{_filteredItems.Count} item{(_filteredItems.Count != 1 ? "s" : "")}");
      ImGui.Separator();

      if (ImGui.BeginChild("##ItemList", new Vector2(0, 0), false)) {
        for (int i = 0; i < _filteredItems.Count; i++) {
          DrawMediaItem(_filteredItems[i], i);
        }
      }
      ImGui.EndChild();
    }

    private void DrawMediaItem(MediaCatalogItem item, int index) {
      ImGui.PushID(index);

      float availWidth = ImGui.GetContentRegionAvail().X;
      float rowHeight = 52;

      // Clickable row
      Vector2 cursorPos = ImGui.GetCursorScreenPos();
      bool clicked = ImGui.InvisibleButton("##item", new Vector2(availWidth, rowHeight));
      ImGui.SetItemAllowOverlap();

      // Hover highlight
      bool hovered = ImGui.IsItemHovered();
      if (hovered) {
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(cursorPos,
          new Vector2(cursorPos.X + availWidth, cursorPos.Y + rowHeight),
          ImGui.GetColorU32(new Vector4(0.3f, 0.5f, 0.7f, 0.3f)));
      }

      // Draw content over the invisible button
      ImGui.SetCursorScreenPos(cursorPos + new Vector2(8, 4));

      // Title line
      ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), item.Title);

      // Info line
      ImGui.SetCursorScreenPos(cursorPos + new Vector2(8, 24));
      var infoColor = new Vector4(0.6f, 0.6f, 0.6f, 1f);
      string info = "";
      if (!string.IsNullOrEmpty(item.Uploader)) info += item.Uploader;
      if (!string.IsNullOrEmpty(item.DurationFormatted)) {
        if (info.Length > 0) info += " • ";
        info += item.DurationFormatted;
      }
      if (!string.IsNullOrEmpty(item.Category) && string.IsNullOrEmpty(_selectedCategory)) {
        if (info.Length > 0) info += " • ";
        info += item.Category;
      }
      ImGui.TextColored(infoColor, info);

      // Play button on the right
      float buttonWidth = 50;
      ImGui.SetCursorScreenPos(new Vector2(cursorPos.X + availWidth - buttonWidth - 8, cursorPos.Y + 12));
      if (ImGui.SmallButton("Play")) {
        PlayItem(item);
      }

      if (clicked) {
        PlayItem(item);
      }

      // Tooltip
      if (hovered && !string.IsNullOrEmpty(item.Description)) {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(300);
        ImGui.TextWrapped(item.Description);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
      }

      // Separator
      ImGui.SetCursorScreenPos(new Vector2(cursorPos.X, cursorPos.Y + rowHeight));
      ImGui.Separator();

      ImGui.PopID();
    }

    private void PlayItem(MediaCatalogItem item) {
      _statusMessage = $"Playing: {item.Title}";
      OnPlayRequested?.Invoke(this, item);
    }

    private void LoadCatalog() {
      if (_isLoading || _selectedProviderIndex >= _providers.Count) return;
      _isLoading = true;
      _statusMessage = "Loading...";

      var provider = _providers[_selectedProviderIndex];
      Task.Run(async () => {
        try {
          _currentCatalog = await provider.FetchCatalog();
          ApplyFilter();
          _statusMessage = _currentCatalog != null
            ? $"Loaded {_currentCatalog.Items.Count} items"
            : "Failed to load catalog";
        } catch (Exception e) {
          _statusMessage = $"Error: {e.Message}";
        } finally {
          _isLoading = false;
        }
      });
    }

    private void RefreshCatalog() {
      if (_isLoading || _selectedProviderIndex >= _providers.Count) return;
      _isLoading = true;
      _currentCatalog = null;
      _statusMessage = "Refreshing...";

      var provider = _providers[_selectedProviderIndex];
      Task.Run(async () => {
        try {
          await provider.Refresh();
          _currentCatalog = await provider.FetchCatalog();
          ApplyFilter();
          _statusMessage = _currentCatalog != null
            ? $"Loaded {_currentCatalog.Items.Count} items"
            : "Failed to load catalog";
        } catch (Exception e) {
          _statusMessage = $"Error: {e.Message}";
        } finally {
          _isLoading = false;
        }
      });
    }

    private void ApplyFilter() {
      if (_currentCatalog == null) {
        _filteredItems.Clear();
        return;
      }

      IEnumerable<MediaCatalogItem> items;
      if (!string.IsNullOrEmpty(_selectedCategory)) {
        items = _currentCatalog.GetByCategory(_selectedCategory);
      } else {
        items = _currentCatalog.Items;
      }

      if (!string.IsNullOrWhiteSpace(_searchQuery)) {
        string q = _searchQuery.ToLowerInvariant();
        items = items.Where(i =>
          (i.Title?.ToLowerInvariant().Contains(q) == true) ||
          (i.Description?.ToLowerInvariant().Contains(q) == true) ||
          (i.Uploader?.ToLowerInvariant().Contains(q) == true));
      }

      _filteredItems = items.ToList();
    }
  }
}
