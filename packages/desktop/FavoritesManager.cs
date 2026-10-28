using System.Text.Json;

namespace AgBrowser.Desktop;

internal sealed class FavoriteTags
{
    private readonly string path;
    private Dictionary<string, string[]> values = new(StringComparer.Ordinal);
    private bool readable = true;
    public FavoriteTags(string directory)
    {
        path = Path.Combine(directory, "favorite-tags.json");
        try { if (File.Exists(path)) values = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path)) ?? throw new JsonException(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { readable = false; }
    }
    public string[] Get(string url) => values.TryGetValue(url, out var tags) && tags != null ? tags : [];
    // Set publishes a new dictionary instead of mutating the old one, so a
    // background search can safely keep its own point-in-time view.
    public IReadOnlyDictionary<string, string[]> Snapshot() => values;
    public void Set(string url, string text)
    {
        if (!readable) throw new IOException("标签文件无法读取，已保留原文件，暂不能保存。");
        string[] tags = text.Split([',', '，', ';', '；'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (tags.Length > 20 || tags.Any(x => x.Length > 40)) throw new ArgumentException("每个收藏最多 20 个标签，每个标签最多 40 个字符。");
        var updated = new Dictionary<string, string[]>(values, StringComparer.Ordinal);
        if (tags.Length == 0) updated.Remove(url); else updated[url] = tags;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(updated)); File.Move(temp, path, true); values = updated;
    }
}

internal sealed class FavoriteListView : ListView
{
    public FavoriteListView() { DoubleBuffered = true; }
    public bool ShowKeyboardFocus => ShowFocusCues;
}

public sealed partial class BrowserWindow
{
    private FavoriteTags? favoriteTags;
    private static readonly SemaphoreSlim FavoriteIconGate = new(1, 1);
    private event Action? favoriteIconsChanged;
    private FavoriteTags Tags => favoriteTags ??= new FavoriteTags(dataDir);

    private async Task LoadFavoriteIcons(FavoriteSnapshot snapshot, CancellationToken cancellation)
    {
        try
        {
            await FavoriteIconGate.WaitAsync(cancellation);
            (Dictionary<string, byte[]> Icons, Dictionary<string, List<string>> PageIndex, IReadOnlyDictionary<string, byte[]> Saved) loaded;
            try { loaded = await Task.Run(() => (FavoriteIcons.Read(snapshot, cancellation), FavoriteIcons.BuildPageIndex(snapshot, cancellation), favoritePageIconCache.Snapshot()), cancellation); }
            finally { FavoriteIconGate.Release(); }
            if (cancellation.IsCancellationRequested || IsDisposed || !ReferenceEquals(displayedFavorites, snapshot)) return;
            if (loaded.Saved.Count > 0)
                foreach (var item in loaded.PageIndex)
                    if (loaded.Saved.TryGetValue(FavoritePageIconCache.HashPageKey(item.Key), out var saved))
                        foreach (string alias in item.Value) loaded.Icons[alias] = saved;
            bool save = false;
            foreach (var tab in tabs)
            {
                if (string.IsNullOrEmpty(tab.IconData) || tab.IconData.Length > 180_000 || tab.IconSource is not { } iconSource || !FavoriteIcons.TryPageKey(iconSource, out var key) ||
                    !loaded.PageIndex.TryGetValue(key, out var aliases)) continue;
                try
                {
                    byte[] bytes = Convert.FromBase64String(tab.IconData);
                    if (bytes.Length is 0 or > 131072) continue;
                    foreach (string alias in aliases) loaded.Icons[alias] = bytes;
                    if (favoritePageIconCache.Record(key, bytes)) save = true;
                }
                catch (FormatException) { }
            }
            foreach (var item in loaded.Icons.ToArray())
                if (Uri.TryCreate(item.Key, UriKind.Absolute, out var uri)) loaded.Icons.TryAdd(uri.AbsoluteUri, item.Value);
            favoritePageIndex = loaded.PageIndex;
            favoriteIcons = loaded.Icons;
            if (save) { favoriteIconSaveTimer.Stop(); favoriteIconSaveTimer.Start(); }
            foreach (var image in suggestionIcons.Values) image.Dispose(); suggestionIcons.Clear();
            if (suggestionPopup.Visible) addressSuggestions.Invalidate();
            void Apply(IEnumerable<ToolStripItem> items)
            {
                foreach (var item in items)
                {
                    if (item is ToolStripButton button && button.Tag is string url) ApplyFavoriteIcon(button, url);
                    if (item is ToolStripDropDownItem folder && folder.HasDropDownItems) Apply(folder.DropDownItems.Cast<ToolStripItem>());
                }
            }
            Apply(favoritesBar.Items.Cast<ToolStripItem>());
            favoriteIconsChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
    }

    private void ApplyFavoriteIcon(ToolStripButton button, string url, bool replace = false)
    {
        if (button.IsDisposed || (!replace && button.Image != null && !ReferenceEquals(button.Image.Tag, FavoriteGlyphs.FallbackMarker)) || !favoriteIcons.TryGetValue(url, out var bytes)) return;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false); using var image = Image.FromStream(stream);
            var previous = button.Image; button.Image = new Bitmap(image, new Size(16, 16)); previous?.Dispose(); button.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException) { }
    }

    private void ApplyLiveFavoriteIcon(string url, byte[] bytes)
    {
        if (bytes.Length is 0 or > 131072 || !FavoriteIcons.TryPageKey(url, out var key) || !favoritePageIndex.TryGetValue(key, out var aliases)) return;
        if (favoritePageIconCache.Record(key, bytes)) { favoriteIconSaveTimer.Stop(); favoriteIconSaveTimer.Start(); }
        var targets = new HashSet<string>(aliases, StringComparer.Ordinal);
        foreach (string alias in aliases)
        {
            favoriteIcons[alias] = bytes;
            if (Uri.TryCreate(alias, UriKind.Absolute, out var uri)) favoriteIcons[uri.AbsoluteUri] = bytes;
        }
        foreach (var entry in suggestionIcons.ToArray())
        {
            if (!FavoriteIcons.TryPageKey(entry.Key, out var candidate) || candidate != key) continue;
            suggestionIcons.Remove(entry.Key); entry.Value.Dispose();
        }
        void Apply(IEnumerable<ToolStripItem> items)
        {
            foreach (var item in items)
            {
                if (item is ToolStripButton button && button.Tag is string target && targets.Contains(target))
                    ApplyFavoriteIcon(button, target, replace: true);
                if (item is ToolStripDropDownItem folder && folder.HasDropDownItems) Apply(folder.DropDownItems.Cast<ToolStripItem>());
            }
        }
        Apply(favoritesBar.Items.Cast<ToolStripItem>());
        if (suggestionPopup.Visible) addressSuggestions.Invalidate();
        favoriteIconsChanged?.Invoke();
    }

    private void EditFavoriteTags(FavoriteNode node, IWin32Window? owner = null)
    {
        if (node.Url is not { } url) return;
        using var dialog = new ChromeDialog("收藏标签", new Size(500, 225));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(26, 0, 26, 16) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var input = new ChromeInput { Dock = DockStyle.Fill }; var field = input.Field; field.Text = string.Join(", ", Tags.Get(url)); field.AccessibleName = "收藏标签，以逗号分隔";
        var save = new ChromeButton { Text = "保存", Anchor = AnchorStyles.Right };
        save.Click += (_, _) => { try { Tags.Set(url, field.Text); dialog.DialogResult = DialogResult.OK; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { ChromeMessage.Show(dialog, ex.Message, "收藏标签", MessageBoxButtons.OK, MessageBoxIcon.Warning); } };
        layout.Controls.Add(new Label { Text = "以逗号分隔标签；清空可移除标签。标签保存在本机。", Dock = DockStyle.Fill }, 0, 0); layout.Controls.Add(input, 0, 1); layout.Controls.Add(save, 0, 2); dialog.Body.Controls.Add(layout); dialog.AcceptButton = save; dialog.ShowChromeDialog(owner ?? this);
    }

    private sealed record FavoriteRow(FavoriteNode Node, string Display);
    private void ShowFavoritesManager()
    {
        if (displayedFavorites is not { } snapshot) return;
        IEnumerable<FavoriteNode> Walk(IEnumerable<FavoriteNode> nodes)
        {
            foreach (var node in nodes) { if (node.Url != null) yield return node; foreach (var child in Walk(node.Children)) yield return child; }
        }
        var all = Walk(snapshot.EnumerateRoots()).ToArray();
        using var dialog = new ChromeDialog("收藏管理", new Size(760, 540), resizable: true) { MinimumSize = new Size(520, 400) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(20, 0, 20, 16) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        var searchInput = new ChromeInput { Dock = DockStyle.Fill }; var search = searchInput.Field; search.PlaceholderText = "搜索名称、网址或标签"; search.AccessibleName = "搜索收藏";
        var list = new FavoriteListView { Dock = DockStyle.Fill, View = View.Details, HeaderStyle = ColumnHeaderStyle.None,
            BorderStyle = BorderStyle.None, VirtualMode = true, OwnerDraw = true, FullRowSelect = true, MultiSelect = false,
            HideSelection = false, ShowItemToolTips = true, AccessibleName = "收藏搜索结果" };
        list.Columns.Add("收藏", 100);
        void ResizeColumn() => list.Columns[0].Width = Math.Max(1, list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - LogicalToDeviceUnits(2));
        list.Resize += (_, _) => ResizeColumn();
        using var icons = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(LogicalToDeviceUnits(24), LogicalToDeviceUnits(30)) };
        using var siteIcon = FavoriteGlyphs.Site();
        using var rowImage = new Bitmap(icons.ImageSize.Width, icons.ImageSize.Height);
        icons.Images.Add(rowImage); list.SmallImageList = icons;
        var info = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = new ChromeButton { Text = "关闭", DialogResult = DialogResult.Cancel };
        var open = new ChromeButton { Text = "打开", Primary = true, Enabled = false }; var tag = new ChromeButton { Text = "编辑标签", Enabled = false };
        buttons.Controls.Add(open); buttons.Controls.Add(tag); buttons.Controls.Add(close);
        layout.Controls.Add(searchInput, 0, 0); layout.Controls.Add(list, 0, 1); layout.Controls.Add(info, 0, 2); layout.Controls.Add(buttons, 0, 3); dialog.Body.Controls.Add(layout); dialog.AcceptButton = open; dialog.CancelButton = close;
        using var debounce = new System.Windows.Forms.Timer { Interval = 120 };
        FavoriteRow[] rows = [];
        CancellationTokenSource? pendingSearch = null;
        int searchGeneration = 0;
        list.RetrieveVirtualItem += (_, e) => {
            string text = e.ItemIndex >= 0 && e.ItemIndex < rows.Length ? rows[e.ItemIndex].Display : "";
            e.Item = new ListViewItem(text, 0) { ToolTipText = text };
        };
        list.DrawItem += (_, e) => {
            bool selected = list.SelectedIndices.Count > 0 && list.SelectedIndices[0] == e.ItemIndex;
            Color background = SystemInformation.HighContrast ? (selected ? SystemColors.Highlight : SystemColors.Window) : selected ? Color.FromArgb(232, 240, 254) : Color.White;
            using var fill = new SolidBrush(background); e.Graphics.FillRectangle(fill, e.Bounds);
            if (selected && list.Focused && list.ShowKeyboardFocus) ControlPaint.DrawFocusRectangle(e.Graphics, e.Bounds);
        };
        list.DrawSubItem += (_, e) => {
            if (e.ItemIndex < 0 || e.ItemIndex >= rows.Length) return;
            var row = rows[e.ItemIndex];
            bool selected = list.SelectedIndices.Count > 0 && list.SelectedIndices[0] == e.ItemIndex;
            Color ink = SystemInformation.HighContrast ? (selected ? SystemColors.HighlightText : SystemColors.WindowText) : Color.FromArgb(32, 33, 36);
            Color secondary = SystemInformation.HighContrast ? ink : Color.FromArgb(95, 99, 104);
            int iconSize = LogicalToDeviceUnits(16), left = e.Bounds.Left + LogicalToDeviceUnits(8);
            var icon = GetSuggestionIcon(new AddressSuggestion(row.Node.Name, row.Node.Url!, true, 0, 0)) ?? siteIcon;
            e.Graphics.DrawImage(icon, new Rectangle(left, e.Bounds.Top + (e.Bounds.Height - iconSize) / 2, iconSize, iconSize));
            left += LogicalToDeviceUnits(26);
            int available = Math.Max(0, e.Bounds.Right - left - LogicalToDeviceUnits(8));
            int titleWidth = Math.Min(available * 2 / 5, TextRenderer.MeasureText(e.Graphics, row.Node.Name, list.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width);
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(e.Graphics, row.Node.Name, list.Font, new Rectangle(left, e.Bounds.Top, titleWidth, e.Bounds.Height), ink, flags);
            int suffixLeft = left + titleWidth + LogicalToDeviceUnits(10);
            if (suffixLeft < e.Bounds.Right)
                TextRenderer.DrawText(e.Graphics, row.Display[row.Node.Name.Length..].TrimStart(), list.Font, new Rectangle(suffixLeft, e.Bounds.Top, e.Bounds.Right - suffixLeft - LogicalToDeviceUnits(8), e.Bounds.Height), secondary, flags);
        };
        void RefreshIcons() { if (!list.IsDisposed) list.Invalidate(); }
        favoriteIconsChanged += RefreshIcons;
        FavoriteNode? SelectedNode()
        {
            if (list.SelectedIndices.Count == 0) return null;
            int index = list.SelectedIndices[0];
            return index >= 0 && index < rows.Length ? rows[index].Node : null;
        }
        list.SelectedIndexChanged += (_, _) => { bool selected = SelectedNode() is not null; open.Enabled = selected; tag.Enabled = selected; };
        void CancelSearch() { searchGeneration++; pendingSearch?.Cancel(); }
        async void Refresh()
        {
            CancelSearch();
            int generation = searchGeneration;
            var cancellation = new CancellationTokenSource(); pendingSearch = cancellation;
            string query = search.Text.Trim();
            var tagsSnapshot = Tags.Snapshot();
            info.Text = "正在搜索收藏…";
            try
            {
                var result = await Task.Run(() => {
                    var found = new List<FavoriteRow>();
                    foreach (var node in all)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        string tags = tagsSnapshot.TryGetValue(node.Url!, out var value) && value != null ? string.Join(", ", value) : "";
                        if (query.Length > 0 && !node.Name.Contains(query, StringComparison.OrdinalIgnoreCase) && !node.Url!.Contains(query, StringComparison.OrdinalIgnoreCase) && !tags.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                        found.Add(new FavoriteRow(node, $"{node.Name}  {node.Url}" + (tags.Length == 0 ? "" : $"   [{tags}]")));
                    }
                    return found.ToArray();
                }, cancellation.Token);
                if (dialog.IsDisposed || cancellation.IsCancellationRequested || generation != searchGeneration) return;
                list.BeginUpdate();
                try { list.VirtualListSize = 0; rows = result; list.VirtualListSize = rows.Length; ResizeColumn(); }
                finally { list.EndUpdate(); }
                info.Text = $"{rows.Length} / {all.Length} 项；标签仅保存在 agbrowser 本机。";
            }
            catch (OperationCanceledException) { }
            finally { if (ReferenceEquals(pendingSearch, cancellation)) pendingSearch = null; cancellation.Dispose(); }
        }
        debounce.Tick += (_, _) => { debounce.Stop(); Refresh(); };
        search.TextChanged += (_, _) => {
            CancelSearch(); debounce.Stop();
            rows = []; list.VirtualListSize = 0; open.Enabled = false; tag.Enabled = false;
            info.Text = "正在搜索收藏…"; debounce.Start();
        };
        dialog.FormClosed += (_, _) => { debounce.Stop(); CancelSearch(); favoriteIconsChanged -= RefreshIcons; };
        void OpenSelected() { if (SelectedNode() is { } node) { dialog.Close(); _ = AddTab(node.Url); } }
        open.Click += (_, _) => OpenSelected(); list.DoubleClick += (_, _) => OpenSelected();
        tag.Click += (_, _) => { if (SelectedNode() is { } node) { EditFavoriteTags(node, dialog); Refresh(); } };
        try { Refresh(); dialog.ShowChromeDialog(this); }
        finally { debounce.Stop(); CancelSearch(); favoriteIconsChanged -= RefreshIcons; }
    }
}
