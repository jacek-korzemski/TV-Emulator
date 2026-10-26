using System.Windows;
using TvEmulator.Models;
using TvEmulator.Services;

namespace TvEmulator;

public partial class SettingsWindow : Window
{
    private readonly string _root;
    private readonly UserSettings _source;
    private readonly List<ProfileToggle> _toggles;

    public UserSettings Result { get; private set; }

    public SettingsWindow(string root, IReadOnlyList<TvProfile> profiles, IReadOnlyList<EngineBuild> engines, UserSettings current)
    {
        InitializeComponent();
        _root = root;
        _source = current;
        Result = current;
        AppBox.Text = current.DefaultApp;
        var hidden = new HashSet<string>(
            (current.HiddenProfiles ?? new List<string>()).Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);
        _toggles = profiles
            .Select(profile => new ProfileToggle(
                profile.Id,
                profile.Label,
                Detail(profile, engines),
                !hidden.Contains(profile.Id)))
            .ToList();
        ProfileList.ItemsSource = _toggles;
        UpdateCount();
    }

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Domyślny katalog aplikacji HTML",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(AppBox.Text) ? AppBox.Text : _root
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            AppBox.Text = dialog.SelectedPath;
    }

    private void ShowAllClick(object sender, RoutedEventArgs e) => SetAll(true);

    private void HideAllClick(object sender, RoutedEventArgs e) => SetAll(false);

    private void ProfileToggled(object sender, RoutedEventArgs e) => UpdateCount();

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (_toggles.Count > 0 && _toggles.All(item => !item.Visible))
        {
            SettingsStatus.Text = "Zostaw włączony przynajmniej jeden profil.";
            return;
        }

        Result = new UserSettings
        {
            DefaultApp = AppBox.Text.Trim(),
            HiddenProfiles = _toggles.Where(item => !item.Visible).Select(item => item.Id).ToList(),
            CpuClockGhz = _source.CpuClockGhz,
            Budgets = _source.Budgets ?? new List<ProfileBudget>()
        };
        DialogResult = true;
    }

    private void SetAll(bool visible)
    {
        foreach (var item in _toggles)
            item.Visible = visible;
        ProfileList.ItemsSource = null;
        ProfileList.ItemsSource = _toggles;
        UpdateCount();
    }

    private void UpdateCount()
    {
        var visible = _toggles.Count(item => item.Visible);
        ProfileCountText.Text = "Widoczne na liście startowej: " + visible + " z " + _toggles.Count + ".";
        if (visible > 0 && SettingsStatus.Text.Length > 0)
            SettingsStatus.Text = "";
    }

    private static string Detail(TvProfile profile, IReadOnlyList<EngineBuild> engines)
    {
        var engine = engines.FirstOrDefault(item => item.Id == profile.Engine);
        var name = engine is null ? profile.Engine : "Chromium " + engine.Milestone;
        return string.IsNullOrWhiteSpace(profile.Player) ? name : name + " · " + profile.Player;
    }

    private sealed class ProfileToggle(string id, string label, string detail, bool visible)
    {
        public string Id { get; } = id;
        public string Label { get; } = label;
        public string Detail { get; } = detail;
        public bool Visible { get; set; } = visible;
    }
}
