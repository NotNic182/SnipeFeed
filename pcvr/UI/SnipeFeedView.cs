using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.Components.Settings;
using HMUI;
using SnipeFeed.PC.Configuration;
using SnipeFeed.PC.Models;
using SnipeFeed.PC.Services;
using SnipeFeed.PC.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SnipeFeed.PC.UI
{
    // The Snipe Feed gameplay setup tab, mirroring the Quest mod's
    // FeedView: a filter/count/refresh control row, a muted status line, a
    // BeatLeader-style score list, and a detail modal with cover art,
    // avatar and the play/download button. This object outlives the tab's
    // GameObjects (BSML re-parses the same host on every menu rebuild), so
    // the feed survives menu visits just like the Quest mod's static state.
    internal sealed class SnipeFeedView
    {
        private const string AllPlayers = "All players";
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);

        // Vertical space the control row + status line take above the list;
        // the list is sized to (measured tab height - HeaderHeight). Keep in
        // tune with the header in SnipeFeedView.bsml.
        private const float HeaderHeight = 13.5f;

        // While an image is loading (or failed) it shows as a dim tile
        // instead of a stark white square. Shared by rows and the modal.
        internal static readonly Color PlaceholderTint = new Color(1f, 1f, 1f, 0.15f);
        internal static readonly Color LoadedTint = Color.white;

        private readonly BeatLeaderService _service = new BeatLeaderService();
        private readonly List<FeedEntry> _entries = new List<FeedEntry>();
        private readonly List<FeedEntry> _visibleEntries = new List<FeedEntry>();
        private List<object> _feedRows = new List<object>();
        private List<object> _filterOptions = new List<object> { AllPlayers };
        private string _selectedFilter = AllPlayers;
        private FeedEntry _selected;
        private DateTimeOffset _lastFetch = DateTimeOffset.MinValue;
        private bool _refreshing;
        private bool _installing;

        // Stale-guards for the modal images: a second selection before a
        // download lands must not let the first score's art pop in.
        private string _pendingCoverUrl;
        private string _pendingAvatarUrl;

        private Transform _modalOriginalParent;

        [UIObject("root")]
        private GameObject _rootObject;

        [UIComponent("feed-list")]
        private CustomCellListTableData _feedList;

        [UIComponent("status")]
        private TextMeshProUGUI _status;

        [UIComponent("player-filter")]
        private DropDownListSetting _playerFilter;

        [UIComponent("feed-count-setting")]
        private IncrementSetting _feedCountSetting;

        [UIComponent("detail-modal")]
        private ModalView _detailModal;

        [UIComponent("modal-cover")]
        private ImageView _modalCover;

        [UIComponent("modal-avatar")]
        private ImageView _modalAvatar;

        [UIComponent("modal-detail")]
        private TextMeshProUGUI _modalDetail;

        [UIComponent("modal-player")]
        private TextMeshProUGUI _modalPlayer;

        [UIComponent("play-button")]
        private NoTransitionsButton _playButton;

        [UIValue("feed-cells")]
        public List<object> FeedRows => _feedRows;

        [UIValue("filter-options")]
        public List<object> FilterOptions => _filterOptions;

        [UIValue("selected-filter")]
        public string SelectedFilter
        {
            get => _selectedFilter;
            set
            {
                _selectedFilter = string.IsNullOrEmpty(value) ? AllPlayers : value;
                RebuildList();
            }
        }

        [UIValue("feed-count")]
        public int FeedCount
        {
            get => Math.Max(10, Math.Min(100, PluginConfig.Instance?.FeedCount ?? 50));
            set
            {
                if (PluginConfig.Instance != null)
                    PluginConfig.Instance.FeedCount = Math.Max(10, Math.Min(100, value));
            }
        }

        [UIAction("#post-parse")]
        private void PostParse()
        {
            if (_detailModal != null)
                _modalOriginalParent = _detailModal.transform.parent;

            CompactHeaderRow();

            // Re-run the refresh/rebuild logic whenever the tab becomes
            // visible again (BSML only parses once per menu build, but the
            // tab GameObject is toggled on every tab switch and menu visit).
            if (_rootObject != null)
            {
                var watcher = _rootObject.AddComponent<TabVisibility>();
                watcher.Shown = OnTabShown;
                watcher.Hidden = OnTabHidden;
                if (_rootObject.activeInHierarchy)
                    OnTabShown();
            }
        }

        // The stock dropdown/increment prefabs assume a full 90-unit
        // settings row (label left, control pinned right). In our compact
        // header row the labels are empty, so stretch the controls over
        // their whole slot instead of leaving them hanging off the edge.
        private void CompactHeaderRow()
        {
            if (_playerFilter != null)
            {
                var rect = _playerFilter.transform as RectTransform;
                if (rect != null)
                {
                    rect.anchorMin = Vector2.zero;
                    rect.anchorMax = Vector2.one;
                    rect.sizeDelta = Vector2.zero;
                    rect.anchoredPosition = Vector2.zero;
                }
                if (_playerFilter.Dropdown != null && _playerFilter.Dropdown._text != null)
                    _playerFilter.Dropdown._text.overflowMode = TextOverflowModes.Ellipsis;
            }

            if (_feedCountSetting != null && _feedCountSetting.transform.childCount > 1)
            {
                var controls = _feedCountSetting.transform.GetChild(1) as RectTransform;
                if (controls != null)
                {
                    controls.anchorMin = Vector2.zero;
                    controls.anchorMax = Vector2.one;
                    controls.sizeDelta = Vector2.zero;
                    controls.anchoredPosition = Vector2.zero;
                }
            }
        }

        private void OnTabShown()
        {
            ResizeListToTab();

            // Defensive: if a hide was ever interrupted (menu hop, tab
            // switch), clear the modal the moment the tab shows again.
            HideModal(false);

            var stale = DateTimeOffset.UtcNow - _lastFetch > CacheLifetime;
            if (stale)
            {
                Refresh();
            }
            else
            {
                UpdateFilters();
                RebuildList();
            }
        }

        private void OnTabHidden()
        {
            HideModal(false);
        }

        // The list viewport must match the real tab height, which varies a
        // little with game version/layout; measure it instead of guessing.
        private void ResizeListToTab()
        {
            if (_rootObject == null || _feedList == null) return;
            var container = _rootObject.transform.parent as RectTransform;
            if (container == null) return;

            var tabHeight = container.rect.height;
            if (tabHeight < 30f) return;

            var layout = _feedList.GetComponent<LayoutElement>();
            if (layout == null) return;

            var target = Mathf.Clamp(tabHeight - HeaderHeight - 1f, 20f, 200f);
            if (Mathf.Abs(layout.preferredHeight - target) < 1f) return;

            layout.preferredHeight = target;
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)_rootObject.transform);
            _feedList.TableView.ReloadData();
        }

        [UIAction("refresh")]
        public async void Refresh()
        {
            if (_refreshing)
            {
                SetStatus("Loading...");
                return;
            }
            _refreshing = true;
            SetStatus("Loading...");

            try
            {
                var config = PluginConfig.Instance ?? new PluginConfig();
                var result = await _service.FetchFeedAsync(
                    config.PlayerId,
                    config.MaxPlayers,
                    config.ScoresPerPlayer,
                    config.FeedCount,
                    SetStatus);

                if (!result.Success)
                {
                    // Keep the previous feed on a failed refresh — replacing
                    // good rows with an empty list turns a network blip into
                    // a broken-looking tab.
                    if (_entries.Count > 0)
                    {
                        SetStatus(result.Error + "\n(Showing the previous scores.)");
                    }
                    else
                    {
                        UpdateFilters();
                        RebuildList();
                        SetStatus(result.Error);
                    }
                    return;
                }

                _entries.Clear();
                _selected = null;
                _entries.AddRange(result.Entries);
                _lastFetch = DateTimeOffset.UtcNow;

                // A successful refresh means the feed (and its images) are
                // current again — let previously-failed images retry.
                SpriteCache.ClearFailures();

                UpdateFilters();
                RebuildList();
                HideModal(false);
                if (_entries.Count == 0 && !string.IsNullOrEmpty(result.Info))
                    SetStatus(result.Info);
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error("UI refresh failed: " + ex);
                SetStatus("Couldn't refresh Snipe Feed: " + Formatting.EscapeForTmp(ex.Message));
            }
            finally
            {
                _refreshing = false;
            }
        }

        private void UpdateFilters()
        {
            _filterOptions = new List<object> { AllPlayers };
            foreach (var name in _entries.Select(x => x.PlayerName).Where(x => !string.IsNullOrEmpty(x)).Distinct())
                _filterOptions.Add(name);

            // Drop a stale filter if that player vanished from the feed.
            if (_selectedFilter != AllPlayers && !_filterOptions.Cast<string>().Contains(_selectedFilter))
                _selectedFilter = AllPlayers;

            if (_playerFilter != null)
            {
                _playerFilter.Values = _filterOptions;
                _playerFilter.UpdateChoices();
                _playerFilter.Value = _selectedFilter;
            }
        }

        private void RebuildList()
        {
            _visibleEntries.Clear();
            _visibleEntries.AddRange(_selectedFilter == AllPlayers
                ? _entries
                : _entries.Where(x => x.PlayerName == _selectedFilter));

            _feedRows = _visibleEntries
                .Select((entry, index) => (object)new FeedRow(entry, index + 1))
                .ToList();

            if (_feedList != null)
            {
                _feedList.Data = _feedRows;
                _feedList.TableView.ReloadData();
                _feedList.TableView.ClearSelection();
            }

            if (_entries.Count > 0)
            {
                if (_selectedFilter == AllPlayers)
                    SetStatus(_entries.Count + " recent scores. Newest first — go snipe!");
                else
                    SetStatus(_visibleEntries.Count + " of " + _entries.Count + " scores by " + _selectedFilter);
            }
            // With no entries, keep whatever error/progress message is showing.
        }

        [UIAction("select-score")]
        private void SelectScore(TableView table, object item)
        {
            table?.ClearSelection();
            var row = item as FeedRow;
            if (row == null) return;
            _selected = row.Entry;

            if (_modalDetail != null) _modalDetail.text = Formatting.DetailText(_selected);
            if (_modalPlayer != null) _modalPlayer.text = _selected.PlayerName;

            // Images, with the same stale-guard the rows use.
            _pendingCoverUrl = _selected.CoverUrl;
            _pendingAvatarUrl = _selected.AvatarUrl;
            ResetImage(_modalCover);
            ResetImage(_modalAvatar);
            if (!string.IsNullOrEmpty(_selected.CoverUrl))
            {
                var url = _selected.CoverUrl;
                SpriteCache.GetSprite(url, sprite =>
                {
                    if (_modalCover == null || _pendingCoverUrl != url) return;
                    _modalCover.sprite = sprite;
                    _modalCover.color = LoadedTint;
                });
            }
            if (!string.IsNullOrEmpty(_selected.AvatarUrl))
            {
                var url = _selected.AvatarUrl;
                SpriteCache.GetSprite(url, sprite =>
                {
                    if (_modalAvatar == null || _pendingAvatarUrl != url) return;
                    _modalAvatar.sprite = sprite;
                    _modalAvatar.color = LoadedTint;
                });
            }

            UpdatePlayButton();

            if (_detailModal != null)
                _detailModal.Show(true, true);
        }

        private void UpdatePlayButton()
        {
            if (_selected == null)
            {
                SetPlayButton("Select a score", false);
                return;
            }
            if (string.IsNullOrWhiteSpace(_selected.SongHash))
            {
                SetPlayButton("Not a custom song", false);
                return;
            }

            // Launching works when the solo flow is on top (re-entry) or a
            // song picker is showing (direct select). In a lobby neither
            // holds: we can download maps but not launch them.
            var canLaunchHere = IsSoloFlowOnTop(out _) || ActivePicker() != null;
            var installed = SongInstaller.GetInstalledLevel(_selected.SongHash) != null;

            string text;
            if (installed)
                text = canLaunchHere ? "Play" : "In Custom Levels";
            else
                text = canLaunchHere ? "Download & Play" : "Download";

            SetPlayButton(text, !_installing && (canLaunchHere || !installed));
        }

        [UIAction("play-selected")]
        private async void PlaySelected()
        {
            if (_installing || _selected == null || string.IsNullOrWhiteSpace(_selected.SongHash)) return;
            var entry = _selected;

            try
            {
                var level = SongInstaller.GetInstalledLevel(entry.SongHash);
                if (level != null)
                {
                    LaunchLevel(level);
                    return;
                }

                _installing = true;
                SetPlayButton("Downloading...", false);

                var install = await SongInstaller.DownloadAndInstallAsync(entry.SongHash);
                if (!install.Success)
                {
                    ShowInstallOutcome(entry, "<color=#ff5555>" + Formatting.EscapeForTmp(install.Error) + "</color>");
                    return;
                }

                level = install.Level ?? SongInstaller.GetInstalledLevel(entry.SongHash);
                if (level == null)
                {
                    ShowInstallOutcome(entry, "Downloaded! The song is still loading — it will appear in Custom Levels shortly.");
                    return;
                }

                // Only auto-launch if the user is still on this tab AND this
                // score is still the selected one — launching score A while
                // score B's modal is open (or from another screen) would yank
                // them somewhere they didn't ask to go.
                if (_rootObject != null && _rootObject.activeInHierarchy && ReferenceEquals(_selected, entry))
                    LaunchLevel(level);
                else
                    SetStatus("Downloaded — press Play when you're back.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error("Download & play failed: " + ex);
                ShowInstallOutcome(entry, "<color=#ff5555>Map install failed: " + Formatting.EscapeForTmp(ex.Message) + "</color>");
            }
            finally
            {
                _installing = false;
                // Recompute the real button state for whatever score is
                // selected NOW instead of hardcoding a label that may belong
                // to a different score.
                if (_selected != null)
                    UpdatePlayButton();
            }
        }

        // Puts an install outcome where the user is actually looking: the
        // modal if this score is still the open one, else the status line.
        private void ShowInstallOutcome(FeedEntry entry, string message)
        {
            if (ReferenceEquals(_selected, entry))
                SetDetail(message);
            else
                SetStatus(message);
        }

        private void LaunchLevel(BeatmapLevel level)
        {
            if (level == null) return;

            // Hide instantly (animated=false): an animated hide would be
            // frozen mid-flight by the flow transitions below, leaving the
            // modal stuck on screen when the panel comes back.
            HideModal(false);

            // In-place selection into the solo picker is NOT a supported
            // game operation: LevelSelectionNavigationController.Setup only
            // stores its "select after present" state and applies it on the
            // next activation. The game's own mechanism is re-entry — and
            // that is only safe when the SOLO flow is what's on top (never
            // from a lobby, where dismissing flows corrupts the menu state).
            if (IsSoloFlowOnTop(out var youngest))
            {
                var parent = youngest._parentFlowCoordinator;
                if (parent != null)
                {
                    try
                    {
                        if (!SongInstaller.PrimeSoloFlow(level))
                        {
                            // The modal is already hidden — leave SOME feedback
                            // instead of silently doing nothing.
                            SetStatus("<color=#ff5555>Couldn't open the song — pick it in Custom Levels.</color>");
                            return;
                        }
                        Plugin.Log?.Info("LaunchLevel: re-entering solo with " + level.songName);
                        parent.DismissFlowCoordinator(
                            youngest,
                            ViewController.AnimationDirection.Horizontal,
                            (Action)(() =>
                            {
                                try { SongInstaller.PressSoloButton(); }
                                catch (Exception ex) { Plugin.Log?.Error("PressSoloButton failed: " + ex); }
                            }),
                            false);
                    }
                    catch (Exception ex)
                    {
                        // An HMUI transition already in progress can throw; a
                        // half-dismissed flow is exactly the menu corruption
                        // this mod promises never to cause.
                        Plugin.Log?.Error("Solo re-entry failed: " + ex);
                        SetStatus("<color=#ff5555>Couldn't open the song — pick it in Custom Levels.</color>");
                    }
                    return;
                }
            }

            // Multiplayer's own song-select screen: its Custom Levels list
            // is the one showing, so a plain SelectLevel works there.
            // Harmless no-op if the level isn't in the shown list.
            var picker = ActivePicker();
            if (picker != null)
            {
                Plugin.Log?.Info("LaunchLevel: selecting in active picker: " + level.songName);
                try
                {
                    picker.SelectLevel(level);
                    SetStatus("Selected in the song list.");
                }
                catch (Exception ex)
                {
                    Plugin.Log?.Warn("Could not select level in active picker: " + ex.Message);
                    SetStatus("Couldn't select the song here — open it from Custom Levels.");
                }
                return;
            }

            // A lobby (vanilla or Multiplayer+) with no picker open. NEVER
            // hijack flows from here — the map is installed; point the
            // player at it.
            SetStatus("Map installed — pick it in the song picker (Custom Levels).");
        }

        private static bool IsSoloFlowOnTop(out FlowCoordinator youngest)
        {
            youngest = null;
            var main = Resources.FindObjectsOfTypeAll<MainFlowCoordinator>().FirstOrDefault();
            if (main == null) return false;
            youngest = main.YoungestChildFlowCoordinatorOrSelf();
            return youngest is SoloFreePlayFlowCoordinator;
        }

        private static LevelCollectionNavigationController ActivePicker()
        {
            return Resources.FindObjectsOfTypeAll<LevelCollectionNavigationController>()
                .FirstOrDefault(x => x != null && x.isActiveAndEnabled);
        }

        private void HideModal(bool animated)
        {
            if (_detailModal == null) return;
            _detailModal.Hide(animated, null);
            // Reparent back inline: Show(_, moveToCenter: true) moved the
            // modal under the shared center container, and HMUI does not
            // reliably invoke a hide callback for a modal that a click-off
            // already hid — relying on the callback leaks the modal into the
            // shared container across menu rebuilds. Every caller passes
            // animated=false, so an immediate reparent cannot fight an
            // animation.
            if (_modalOriginalParent != null && _detailModal.transform.parent != _modalOriginalParent)
                _detailModal.transform.SetParent(_modalOriginalParent, true);
        }

        private static void ResetImage(ImageView image)
        {
            if (image == null) return;
            image.sprite = null;
            image.color = PlaceholderTint;
        }

        private void SetPlayButton(string text, bool interactable)
        {
            if (_playButton == null) return;
            _playButton.interactable = interactable;
            var label = _playButton.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null) label.text = text;
        }

        private void SetStatus(string text)
        {
            if (_status != null) _status.text = text ?? "";
        }

        private void SetDetail(string text)
        {
            if (_modalDetail != null) _modalDetail.text = text ?? "";
        }

        // One feed entry as a BeatLeader-style row. BSML parses the cell
        // template with this object as host each time the row is shown.
        private sealed class FeedRow
        {
            public readonly FeedEntry Entry;
            private readonly int _rank;

            [UIComponent("cover-image")]
            private ImageView _cover;

            [UIComponent("avatar-image")]
            private ImageView _avatar;

            [UIObject("song-line")]
            private GameObject _songLine;

            public FeedRow(FeedEntry entry, int rank)
            {
                Entry = entry;
                _rank = rank;
            }

            [UIValue("rank-text")]
            public string RankText => Formatting.RankText(_rank);

            [UIValue("song-text")]
            public string SongText => Formatting.TitleLine(Entry);

            [UIValue("player-text")]
            public string PlayerText => Formatting.PlayerLine(Entry);

            [UIValue("stats-text")]
            public string StatsText => Formatting.StatsLine(Entry);

            [UIValue("time-text")]
            public string TimeText => Formatting.TimeAgo(Entry.Timepost);

            [UIAction("#post-parse")]
            private void PostParse()
            {
                // Hard-clips overlong titles at the wrapper bounds; the
                // title text itself has no TMP overflow mode (see the BSML).
                if (_songLine != null && _songLine.GetComponent<RectMask2D>() == null)
                    _songLine.AddComponent<RectMask2D>();

                ResetImage(_cover);
                ResetImage(_avatar);
                LoadInto(_cover, Entry.CoverUrl);
                LoadInto(_avatar, Entry.AvatarUrl);
            }

            private static void LoadInto(ImageView target, string url)
            {
                if (target == null || string.IsNullOrEmpty(url)) return;
                SpriteCache.GetSprite(url, sprite =>
                {
                    // The row may have been re-parsed into a fresh cell by
                    // the time the download lands; only touch the ImageView
                    // this call was bound to, and only while it's alive.
                    if (target == null) return;
                    target.sprite = sprite;
                    target.color = LoadedTint;
                });
            }
        }

        // Relays the tab GameObject's visibility to the view; BSML has no
        // per-activation callback for gameplay setup tabs on PC.
        private sealed class TabVisibility : MonoBehaviour
        {
            public Action Shown;
            public Action Hidden;

            private void OnEnable() => Shown?.Invoke();

            private void OnDisable() => Hidden?.Invoke();
        }
    }
}
