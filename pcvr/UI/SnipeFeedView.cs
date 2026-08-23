using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.Components.Settings;
using HMUI;
using SnipeFeed.PC.Configuration;
using SnipeFeed.PC.Models;
using SnipeFeed.PC.Services;
using SnipeFeed.PC.Utils;
using SongCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SnipeFeed.PC.UI
{
    internal sealed class SnipeFeedView
    {
        private const string AllPlayers = "All players";
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);
        private readonly BeatLeaderService _service = new BeatLeaderService();
        private readonly List<FeedEntry> _entries = new List<FeedEntry>();
        private readonly List<FeedEntry> _visibleEntries = new List<FeedEntry>();
        private List<CustomListTableData.CustomCellInfo> _feedCells = new List<CustomListTableData.CustomCellInfo>();
        private List<object> _filterOptions = new List<object> { AllPlayers };
        private string _selectedFilter = AllPlayers;
        private FeedEntry _selected;
        private DateTimeOffset _lastFetch = DateTimeOffset.MinValue;
        private bool _refreshing;
        private bool _installing;

        [UIComponent("feed-list")]
        private CustomListTableData _feedList;

        [UIComponent("status")]
        private TextMeshProUGUI _status;

        [UIComponent("detail-text")]
        private TextMeshProUGUI _detailText;

        [UIComponent("play-button")]
        private Button _playButton;

        [UIComponent("player-filter")]
        private DropDownListSetting _playerFilter;

        [UIValue("feed-cells")]
        public List<CustomListTableData.CustomCellInfo> FeedCells => _feedCells;

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
        public float FeedCount
        {
            get => PluginConfig.Instance?.FeedCount ?? 50;
            set
            {
                if (PluginConfig.Instance != null)
                    PluginConfig.Instance.FeedCount = Math.Max(10, Math.Min(100, (int)Math.Round(value)));
            }
        }

        [UIValue("player-id")]
        public string PlayerId
        {
            get => PluginConfig.Instance?.PlayerId ?? "";
            set
            {
                if (PluginConfig.Instance != null)
                    PluginConfig.Instance.PlayerId = (value ?? "").Trim();
            }
        }

        [UIAction("#post-parse")]
        private void PostParse()
        {
            if (_entries.Count == 0 || DateTimeOffset.UtcNow - _lastFetch > CacheLifetime)
                Refresh();
            else
                RebuildList();
        }

        [UIAction("refresh")]
        public async void Refresh()
        {
            if (_refreshing) return;
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

                _entries.Clear();
                if (!result.Success)
                {
                    _selected = null;
                    UpdateFilters();
                    RebuildList();
                    SetStatus(result.Error);
                    SetDetail("Select a score to see details.");
                    UpdatePlayButton();
                    return;
                }

                _entries.AddRange(result.Entries);
                _lastFetch = DateTimeOffset.UtcNow;
                _selected = null;
                UpdateFilters();
                RebuildList();
                SetDetail("Select a score to see details.");
                UpdatePlayButton();
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error("UI refresh failed: " + ex);
                SetStatus("Couldn't refresh Snipe Feed: " + ex.Message);
            }
            finally
            {
                _refreshing = false;
            }
        }

        [UIAction("select-score")]
        private void SelectScore(TableView table, int index)
        {
            if (index < 0 || index >= _visibleEntries.Count) return;
            table?.ClearSelection();
            _selected = _visibleEntries[index];
            SetDetail(Formatting.Detail(_selected));
            UpdatePlayButton();
        }

        [UIAction("play-selected")]
        private async void PlaySelected()
        {
            if (_selected == null || _installing || string.IsNullOrWhiteSpace(_selected.SongHash)) return;

            var level = SongInstaller.GetInstalledLevel(_selected.SongHash);
            if (level == null)
            {
                _installing = true;
                UpdatePlayButton("Downloading...", false);
                SetStatus("Downloading " + _selected.SongName + " from BeatSaver...");

                try
                {
                    var install = await SongInstaller.DownloadAndInstallAsync(_selected.SongHash);
                    if (!install.Success)
                    {
                        SetStatus(install.Error);
                        return;
                    }

                    level = install.Level ?? SongInstaller.GetInstalledLevel(_selected.SongHash);
                    if (level == null)
                    {
                        SetStatus("Downloaded. SongCore is still loading it; it should appear in Custom Levels shortly.");
                        return;
                    }
                }
                finally
                {
                    _installing = false;
                }
            }

            if (TrySelectInActivePicker(level))
                SetStatus("Selected " + _selected.SongName + ". Ready to snipe.");
            else
                SetStatus("Map installed. Open the song picker and select it from Custom Levels.");

            UpdatePlayButton();
        }

        private bool TrySelectInActivePicker(BeatmapLevel level)
        {
            if (level == null) return false;
            try
            {
                var picker = Resources.FindObjectsOfTypeAll<LevelCollectionNavigationController>()
                    .FirstOrDefault(x => x != null && x.isActiveAndEnabled);
                if (picker == null) return false;
                picker.SelectLevel(level);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn("Could not select level in active picker: " + ex.Message);
                return false;
            }
        }

        private void UpdateFilters()
        {
            _filterOptions = new List<object> { AllPlayers };
            foreach (var name in _entries.Select(x => x.PlayerName).Where(x => !string.IsNullOrEmpty(x)).Distinct())
                _filterOptions.Add(name);

            if (_selectedFilter != AllPlayers && !_filterOptions.Cast<string>().Contains(_selectedFilter))
                _selectedFilter = AllPlayers;

            if (_playerFilter != null)
            {
                _playerFilter.Values = _filterOptions;
                _playerFilter.UpdateChoices();
            }
        }

        private void RebuildList()
        {
            _visibleEntries.Clear();
            _visibleEntries.AddRange(_selectedFilter == AllPlayers
                ? _entries
                : _entries.Where(x => x.PlayerName == _selectedFilter));

            _feedCells = _visibleEntries.Select((entry, index) =>
                new CustomListTableData.CustomCellInfo(
                    (index + 1) + ".  " + Formatting.TitleLine(entry),
                    Formatting.SubLine(entry)))
                .ToList();

            if (_feedList != null)
            {
                _feedList.Data = _feedCells;
                _feedList.TableView.ReloadData();
                _feedList.TableView.ClearSelection();
            }

            if (_entries.Count > 0)
            {
                if (_selectedFilter == AllPlayers)
                    SetStatus(_entries.Count + " recent scores. Newest first — go snipe!");
                else
                    SetStatus(_visibleEntries.Count + " of " + _entries.Count + " scores by " + _selectedFilter + ".");
            }
        }

        private void UpdatePlayButton()
        {
            if (_selected == null)
            {
                UpdatePlayButton("Select a score", false);
                return;
            }
            if (string.IsNullOrWhiteSpace(_selected.SongHash))
            {
                UpdatePlayButton("Not a custom song", false);
                return;
            }
            if (_installing)
            {
                UpdatePlayButton("Downloading...", false);
                return;
            }

            var installed = SongInstaller.GetInstalledLevel(_selected.SongHash) != null;
            var pickerOpen = Resources.FindObjectsOfTypeAll<LevelCollectionNavigationController>()
                .Any(x => x != null && x.isActiveAndEnabled);

            if (installed)
                UpdatePlayButton(pickerOpen ? "Select" : "In Custom Levels", pickerOpen);
            else
                UpdatePlayButton(pickerOpen ? "Download & Select" : "Download", true);
        }

        private void UpdatePlayButton(string text, bool interactable)
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
            if (_detailText != null) _detailText.text = text ?? "";
        }
    }
}
