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
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// BSML assigns the [UIComponent]/[UIObject] fields.
#pragma warning disable CS0649

namespace SnipeFeed.PC.UI
{
    // The Snipe Feed gameplay setup tab, mirroring the Quest mod's
    // FeedView: a Following / My Profile switch, a filter/sort, count and
    // refresh control row, a muted status line, a BeatLeader-style score
    // list, and a detail modal with cover art, skill triangle, avatar and
    // the play/download button. This object outlives the tab's GameObjects
    // (BSML re-parses the same host on every menu rebuild), so both lists
    // survive menu visits just like the Quest mod's static state.
    internal sealed class SnipeFeedView
    {
        private const string AllPlayers = "All players";
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);

        // Vertical space the nav row, control row and status line take above
        // the list; the list is sized to (measured tab height - HeaderHeight).
        // Keep in tune with the header in SnipeFeedView.bsml.
        private const float HeaderHeight = 22f;

        // Rows show the triangle ~7.5 units tall, the modal ~14. The
        // textures have no mipmaps, so draw them near display size.
        private const int RowTrianglePixels = 64;
        private const int ModalTrianglePixels = 128;

        // While an image is loading (or failed) it shows as a dim tile
        // instead of a stark white square. Shared by rows and the modal.
        internal static readonly Color PlaceholderTint = new Color(1f, 1f, 1f, 0.15f);
        internal static readonly Color LoadedTint = Color.white;

        private readonly BeatLeaderService _service = new BeatLeaderService();
        private readonly List<FeedEntry> _entries = new List<FeedEntry>();
        private readonly List<FeedEntry> _profileEntries = new List<FeedEntry>();
        private readonly List<FeedEntry> _visibleEntries = new List<FeedEntry>();
        private List<object> _feedRows = new List<object>();
        private List<object> _filterOptions = new List<object> { AllPlayers };
        private string _selectedFilter = AllPlayers;
        private FeedEntry _selected;
        private DateTimeOffset _lastFetch = DateTimeOffset.MinValue;
        private bool _refreshing;
        private bool _installing;

        private bool _showingProfile;
        private ProfileSummary _profile = new ProfileSummary();
        private int _profileTotalScores;
        private int _profileSort = ProfileSort.Default;
        private bool _profileAscending;
        private DateTimeOffset _lastProfileFetch = DateTimeOffset.MinValue;
        private bool _profileRefreshing;

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

        [UIComponent("order-button")]
        private NoTransitionsButton _orderButton;

        [UIComponent("detail-modal")]
        private ModalView _detailModal;

        [UIComponent("modal-cover")]
        private ImageView _modalCover;

        [UIComponent("modal-triangle")]
        private ImageView _modalTriangle;

        [UIComponent("modal-tech-label")]
        private TextMeshProUGUI _modalTechLabel;

        [UIComponent("modal-acc-label")]
        private TextMeshProUGUI _modalAccLabel;

        [UIComponent("modal-pass-label")]
        private TextMeshProUGUI _modalPassLabel;

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

        // The player filter on Following, the sort on My Profile.
        [UIValue("selected-filter")]
        public string SelectedFilter
        {
            get => _showingProfile ? ProfileSort.Get(_profileSort).Label : _selectedFilter;
            set
            {
                if (_showingProfile)
                {
                    var index = Array.FindIndex(ProfileSort.Options, x => x.Label == value);
                    if (index < 0 || index == _profileSort) return;
                    _profileSort = index;
                    _profileAscending = ProfileSort.Options[index].AscendingByDefault;
                    UpdateOrderButton();
                    RefreshProfile();
                    return;
                }
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

        private List<FeedEntry> CurrentEntries => _showingProfile ? _profileEntries : _entries;

        [UIAction("#post-parse")]
        private void PostParse()
        {
            if (_detailModal != null)
                _modalOriginalParent = _detailModal.transform.parent;

            CompactHeaderRow();
            UpdateOrderButton();
            AttachTriangleLabels(_modalTriangle, _modalTechLabel, _modalAccLabel, _modalPassLabel);

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

            if (IsStale(_showingProfile ? _lastProfileFetch : _lastFetch))
            {
                RefreshCurrent();
            }
            else
            {
                RebuildFilter();
                RebuildList();
            }
        }

        private void OnTabHidden()
        {
            HideModal(false);
        }

        private static bool IsStale(DateTimeOffset lastFetch) => DateTimeOffset.UtcNow - lastFetch > CacheLifetime;

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
        private void RefreshCurrent()
        {
            if (_showingProfile) RefreshProfile();
            else Refresh();
        }

        [UIAction("show-following")]
        private void ShowFollowing()
        {
            if (!_showingProfile) return;
            _showingProfile = false;
            _selected = null;
            HideModal(false);
            RebuildFilter();
            RebuildList();
            if (_entries.Count == 0 || IsStale(_lastFetch)) Refresh();
        }

        [UIAction("show-profile")]
        private void ShowProfile()
        {
            if (_showingProfile) return;
            _showingProfile = true;
            _selected = null;
            HideModal(false);
            RebuildFilter();
            RebuildList();
            if (_profileEntries.Count == 0 || IsStale(_lastProfileFetch)) RefreshProfile();
        }

        [UIAction("toggle-order")]
        private void ToggleOrder()
        {
            _profileAscending = !_profileAscending;
            UpdateOrderButton();
            RefreshProfile();
        }

        private async void Refresh()
        {
            if (_refreshing)
            {
                SetFeedStatus("Loading...");
                return;
            }
            _refreshing = true;
            SetFeedStatus("Loading...");

            try
            {
                var config = PluginConfig.Instance ?? new PluginConfig();
                var result = await _service.FetchFeedAsync(
                    config.PlayerId,
                    config.MaxPlayers,
                    config.ScoresPerPlayer,
                    config.FeedCount,
                    SetFeedStatus);

                if (!result.Success)
                {
                    // Keep the previous feed on a failed refresh — replacing
                    // good rows with an empty list turns a network blip into
                    // a broken-looking tab.
                    if (_entries.Count > 0)
                    {
                        SetFeedStatus(result.Error + "\n(Showing the previous scores.)");
                    }
                    else if (!_showingProfile)
                    {
                        RebuildFilter();
                        RebuildList();
                        SetStatus(result.Error);
                    }
                    return;
                }

                _entries.Clear();
                _entries.AddRange(result.Entries);
                _lastFetch = DateTimeOffset.UtcNow;

                // A successful refresh means the feed (and its images) are
                // current again — let previously-failed images retry.
                SpriteCache.ClearFailures();

                if (!_showingProfile)
                {
                    _selected = null;
                    RebuildFilter();
                    RebuildList();
                    HideModal(false);
                    if (_entries.Count == 0 && !string.IsNullOrEmpty(result.Info))
                        SetStatus(result.Info);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error("UI refresh failed: " + ex);
                SetFeedStatus("Couldn't refresh Snipe Feed: " + Formatting.EscapeForTmp(ex.Message));
            }
            finally
            {
                _refreshing = false;
            }
        }

        private async void RefreshProfile()
        {
            if (_profileRefreshing)
            {
                SetProfileStatus("Loading your profile...");
                return;
            }
            _profileRefreshing = true;
            SetProfileStatus("Loading your profile...");

            var sortIndex = _profileSort;
            var ascending = _profileAscending;
            var sortChanged = false;
            try
            {
                var config = PluginConfig.Instance ?? new PluginConfig();
                var result = await _service.FetchProfileAsync(
                    config.PlayerId,
                    ProfileSort.Query(sortIndex, ascending),
                    config.FeedCount,
                    SetProfileStatus);

                // A sort or direction change while this request was running
                // must not label old-order data as the new order.
                if (sortIndex != _profileSort || ascending != _profileAscending)
                {
                    sortChanged = true;
                    return;
                }

                if (!result.Success)
                {
                    if (_profileEntries.Count > 0)
                    {
                        SetProfileStatus(result.Error + "\n(Showing the previous scores.)");
                    }
                    else if (_showingProfile)
                    {
                        _selected = null;
                        RebuildList();
                        SetStatus(result.Error);
                        HideModal(false);
                    }
                    return;
                }

                _profile = result.Profile;
                _profileEntries.Clear();
                _profileEntries.AddRange(result.Entries);
                _profileTotalScores = result.TotalScores;
                _lastProfileFetch = DateTimeOffset.UtcNow;
                SpriteCache.ClearFailures();

                if (_showingProfile)
                {
                    _selected = null;
                    RebuildFilter();
                    RebuildList();
                    HideModal(false);
                    if (_profileEntries.Count == 0 && !string.IsNullOrEmpty(result.Error))
                        SetStatus(result.Error);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error("Profile refresh failed: " + ex);
                SetProfileStatus("Couldn't load your profile: " + Formatting.EscapeForTmp(ex.Message));
            }
            finally
            {
                _profileRefreshing = false;
                if (sortChanged)
                {
                    if (_showingProfile) RefreshProfile();
                    else _lastProfileFetch = DateTimeOffset.MinValue;
                }
            }
        }

        // The dropdown lists players on Following and sorts on My Profile.
        private void RebuildFilter()
        {
            if (_showingProfile)
            {
                _filterOptions = ProfileSort.Options.Select(x => (object)x.Label).ToList();
            }
            else
            {
                _filterOptions = new List<object> { AllPlayers };
                foreach (var name in _entries.Select(x => x.PlayerName).Where(x => !string.IsNullOrEmpty(x)).Distinct())
                    _filterOptions.Add(name);

                // Drop a stale filter if that player vanished from the feed.
                if (_selectedFilter != AllPlayers && !_filterOptions.Contains(_selectedFilter))
                    _selectedFilter = AllPlayers;
            }

            if (_playerFilter != null)
            {
                _playerFilter.Values = _filterOptions;
                _playerFilter.UpdateChoices();
                _playerFilter.Value = SelectedFilter;
                var hint = _playerFilter.GetComponentInChildren<HoverHint>(true);
                if (hint != null)
                    hint.text = _showingProfile ? "Sort your scores" : "Show scores from one player";
            }
            UpdateOrderButton();
        }

        private void UpdateOrderButton()
        {
            if (_orderButton == null) return;
            _orderButton.gameObject.SetActive(_showingProfile);
            var label = _orderButton.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null) label.text = ProfileSort.OrderLabel(_profileSort, _profileAscending);
        }

        private void RebuildList()
        {
            var entries = CurrentEntries;
            _visibleEntries.Clear();
            _visibleEntries.AddRange(_showingProfile || _selectedFilter == AllPlayers
                ? entries
                : entries.Where(x => x.PlayerName == _selectedFilter));

            var sortBy = _showingProfile ? ProfileSort.Get(_profileSort).SortBy : null;
            _feedRows = _visibleEntries
                .Select((entry, index) => (object)new FeedRow(entry, index + 1, sortBy != null ? Formatting.SortValue(entry, sortBy) : ""))
                .ToList();

            if (_feedList != null)
            {
                _feedList.Data = _feedRows;
                _feedList.TableView.ReloadData();
                _feedList.TableView.ClearSelection();
            }

            // With no entries, keep whatever error/progress message is showing.
            if (entries.Count == 0) return;

            if (_showingProfile)
            {
                var p = _profile;
                var countryRank = !string.IsNullOrEmpty(p.Country) && p.CountryRank > 0
                    ? " • " + Formatting.EscapeForTmp(p.Country) + " #" + p.CountryRank
                    : "";
                var sort = ProfileSort.Get(_profileSort);
                SetStatus(string.Format(CultureInfo.InvariantCulture,
                    "<b>{0}</b> • {1:0.00}pp • Global #{2}{3} • {4:0.00}% ranked avg\n{5} of {6} {7}scores • {8}, {9}",
                    Formatting.EscapeForTmp(p.Name), p.Pp, p.Rank, countryRank, p.AverageRankedAccuracy * 100f,
                    entries.Count, _profileTotalScores, sort.RankedOnly ? "ranked " : "", sort.Label,
                    ProfileSort.OrderLabel(_profileSort, _profileAscending)));
            }
            else if (_selectedFilter == AllPlayers)
            {
                SetStatus(entries.Count + " recent scores. Newest first — go snipe!");
            }
            else
            {
                SetStatus(_visibleEntries.Count + " of " + entries.Count + " scores by " + _selectedFilter);
            }
        }

        [UIAction("select-score")]
        private void SelectScore(TableView table, object item)
        {
            table?.ClearSelection();
            var row = item as FeedRow;
            if (row == null) return;
            _selected = row.Entry;

            if (_modalDetail != null) _modalDetail.text = Formatting.DetailText(_selected);
            if (_modalPlayer != null) _modalPlayer.text = Formatting.EscapeForTmp(_selected.PlayerName);
            if (_modalTriangle != null)
            {
                var triangle = _selected.HasRatings
                    ? SkillTriangle.GetSprite(_selected.PassRating, _selected.AccRating, _selected.TechRating, ModalTrianglePixels)
                    : null;
                _modalTriangle.sprite = triangle;
                // Inactive, so unrated maps leave no gap under the cover.
                // The labels are its children and go with it.
                _modalTriangle.gameObject.SetActive(triangle != null);
                SetTriangleLabels(_selected, true, _modalTechLabel, _modalAccLabel, _modalPassLabel);
            }

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
        // modal if this score is still the open one AND the modal is
        // actually visible, else the status line. Writing into a hidden
        // modal's text would leave the message invisible until the modal
        // happens to reopen on some later, unrelated score.
        private void ShowInstallOutcome(FeedEntry entry, string message)
        {
            if (ReferenceEquals(_selected, entry) && _detailModal != null && _detailModal.isActiveAndEnabled)
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

        // Moves the rating labels onto the triangle's corners: Tech and Acc
        // just above the top corners, Pass just under the bottom point.
        private static void AttachTriangleLabels(ImageView triangle, TextMeshProUGUI tech, TextMeshProUGUI acc, TextMeshProUGUI pass)
        {
            AttachTriangleLabel(triangle, tech, SkillTriangle.TechCorner, new Vector2(0f, 0f), TextAlignmentOptions.BottomLeft);
            AttachTriangleLabel(triangle, acc, SkillTriangle.AccCorner, new Vector2(1f, 0f), TextAlignmentOptions.BottomRight);
            AttachTriangleLabel(triangle, pass, SkillTriangle.PassCorner, new Vector2(0.5f, 1f), TextAlignmentOptions.Top);
        }

        private static void AttachTriangleLabel(ImageView triangle, TextMeshProUGUI label, Vector2 corner, Vector2 pivot, TextAlignmentOptions alignment)
        {
            if (triangle == null || label == null) return;
            var layout = label.GetComponent<LayoutElement>();
            if (layout != null) layout.ignoreLayout = true;

            var rect = label.rectTransform;
            rect.SetParent(triangle.rectTransform, false);
            rect.anchorMin = corner;
            rect.anchorMax = corner;
            rect.pivot = pivot;
            // A small gap so the text doesn't sit on the triangle's outline.
            rect.anchoredPosition = new Vector2(0f, pivot.y > 0.5f ? -0.1f * label.fontSize : 0.1f * label.fontSize);
            rect.sizeDelta = new Vector2(12f, label.fontSize * 2.6f);
            label.alignment = alignment;
            label.overflowMode = TextOverflowModes.Overflow;
            label.raycastTarget = false;
        }

        // Rows show just the numbers; the modal has room for the names too.
        private static void SetTriangleLabels(FeedEntry entry, bool withNames, TextMeshProUGUI tech, TextMeshProUGUI acc, TextMeshProUGUI pass)
        {
            SetTriangleLabel(tech, entry, "Tech", entry.TechRating, withNames);
            SetTriangleLabel(acc, entry, "Acc", entry.AccRating, withNames);
            SetTriangleLabel(pass, entry, "Pass", entry.PassRating, withNames);
        }

        private static void SetTriangleLabel(TextMeshProUGUI label, FeedEntry entry, string name, float rating, bool withName)
        {
            if (label == null) return;
            label.gameObject.SetActive(entry.HasRatings);
            label.text = withName
                ? "<size=85%>" + name + "</size>\n" + rating.ToString("0.00", CultureInfo.InvariantCulture)
                : rating.ToString("0.0", CultureInfo.InvariantCulture);
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

        // Each list's fetch only writes the status line while it's showing.
        private void SetFeedStatus(string text)
        {
            if (!_showingProfile) SetStatus(text);
        }

        private void SetProfileStatus(string text)
        {
            if (_showingProfile) SetStatus(text);
        }

        private void SetDetail(string text)
        {
            if (_modalDetail != null) _modalDetail.text = text ?? "";
        }

        // One score as a BeatLeader-style row. BSML parses the cell
        // template with this object as host each time the row is shown.
        private sealed class FeedRow
        {
            public readonly FeedEntry Entry;
            private readonly int _rank;
            private readonly string _sortValue;

            [UIComponent("cover-image")]
            private ImageView _cover;

            [UIComponent("avatar-image")]
            private ImageView _avatar;

            [UIComponent("triangle-image")]
            private ImageView _triangle;

            [UIComponent("tech-label")]
            private TextMeshProUGUI _techLabel;

            [UIComponent("acc-label")]
            private TextMeshProUGUI _accLabel;

            [UIComponent("pass-label")]
            private TextMeshProUGUI _passLabel;

            [UIComponent("title-text")]
            private TextMeshProUGUI _title;

            [UIObject("song-line")]
            private GameObject _songLine;

            [UIObject("info-line")]
            private GameObject _infoLine;

            // sortValue: what a My Profile row is sorted by
            // (Formatting.SortValue), shown before the time; empty on
            // Following.
            public FeedRow(FeedEntry entry, int rank, string sortValue)
            {
                Entry = entry;
                _rank = rank;
                _sortValue = sortValue ?? "";
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
            public string TimeText
            {
                get
                {
                    var timeAgo = Formatting.TimeAgo(Entry.Timepost);
                    if (_sortValue.Length == 0) return timeAgo;
                    return timeAgo.Length == 0 ? _sortValue : _sortValue + "  " + timeAgo;
                }
            }

            [UIAction("#post-parse")]
            private void PostParse()
            {
                // Hard-clips overlong lines at the wrapper bounds; the texts
                // themselves have no TMP overflow mode (see the BSML).
                foreach (var line in new[] { _songLine, _infoLine })
                {
                    if (line != null && line.GetComponent<RectMask2D>() == null)
                        line.AddComponent<RectMask2D>();
                }

                // Long titles shrink to fit so the difficulty and stars at
                // the end stay visible.
                if (_title != null)
                {
                    _title.enableAutoSizing = true;
                    _title.fontSizeMin = 2.2f;
                    _title.fontSizeMax = 3.4f;
                }

                // No ratings, no triangle; the image stays in the layout so
                // rows keep their columns lined up.
                if (_triangle != null)
                {
                    var triangle = Entry.HasRatings
                        ? SkillTriangle.GetSprite(Entry.PassRating, Entry.AccRating, Entry.TechRating, RowTrianglePixels)
                        : null;
                    _triangle.sprite = triangle;
                    _triangle.color = Color.white;
                    _triangle.enabled = triangle != null;
                    AttachTriangleLabels(_triangle, _techLabel, _accLabel, _passLabel);
                    SetTriangleLabels(Entry, false, _techLabel, _accLabel, _passLabel);
                }

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
