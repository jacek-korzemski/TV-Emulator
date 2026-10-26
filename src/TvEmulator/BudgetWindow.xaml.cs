using System.Globalization;
using System.Windows;
using TvEmulator.Models;
using TvEmulator.Services;

namespace TvEmulator;

public partial class BudgetWindow : Window
{
    private readonly TvProfile _profile;
    private readonly EngineBuild? _engine;
    private readonly UserSettings _source;
    private int? _hintPercent;

    public UserSettings Result { get; private set; }

    public BudgetWindow(TvProfile profile, EngineBuild? engine, UserSettings current)
    {
        InitializeComponent();
        _profile = profile;
        _engine = engine;
        _source = current;
        Result = current;
        ProfileText.Text = profile.Label;
        ThreadsBox.Text = Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture);
        if (current.CpuClockGhz is > 0)
            ClockBox.Text = current.CpuClockGhz.Value.ToString("0.##", CultureInfo.GetCultureInfo("pl-PL"));

        var budget = UserSettingsStore.BudgetFor(current, profile.Id);
        if (budget?.CpuPercent is int cpu)
            CpuBox.Text = cpu.ToString(CultureInfo.InvariantCulture);
        if (budget?.MemoryMb is int memory)
            MemoryBox.Text = memory.ToString(CultureInfo.InvariantCulture);
        RefreshHint();
    }

    private void InputsChanged(object sender, RoutedEventArgs e) => RefreshHint();

    private void ApplyHintClick(object sender, RoutedEventArgs e)
    {
        if (_hintPercent is not int percent)
            return;
        CpuBox.Text = percent.ToString(CultureInfo.InvariantCulture);
        BudgetStatus.Text = "";
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        double? clock = null;
        if (!string.IsNullOrWhiteSpace(ClockBox.Text))
        {
            if (!TvPower.TryParseClock(ClockBox.Text, out var ghz))
            {
                BudgetStatus.Text = "Taktowanie podaj w GHz, na przykład 5,3. Puste pole je czyści.";
                return;
            }

            clock = ghz;
        }

        if (!TvPower.TryReadLimit(CpuBox.Text, 1, 100, out var cpu))
        {
            BudgetStatus.Text = "Procent jednego wątku jest od 1 do 100. Puste pole wyłącza limit.";
            return;
        }

        if (!TvPower.TryReadLimit(MemoryBox.Text, 1, TvPower.MaxMemoryMb, out var memory))
        {
            BudgetStatus.Text = "Pamięć silnika podaj w MB, od 1 w górę. Puste pole wyłącza limit.";
            return;
        }

        var budgets = (_source.Budgets ?? new List<ProfileBudget>())
            .Where(item => !string.Equals(item.ProfileId, _profile.Id, StringComparison.OrdinalIgnoreCase))
            .Select(item => new ProfileBudget
            {
                ProfileId = item.ProfileId,
                CpuPercent = item.CpuPercent,
                MemoryMb = item.MemoryMb
            })
            .ToList();
        if (cpu is not null || memory is not null)
        {
            budgets.Add(new ProfileBudget
            {
                ProfileId = _profile.Id,
                CpuPercent = cpu,
                MemoryMb = memory
            });
        }

        Result = new UserSettings
        {
            DefaultApp = _source.DefaultApp,
            HiddenProfiles = _source.HiddenProfiles?.ToList() ?? new List<string>(),
            CpuClockGhz = clock,
            Budgets = budgets
        };
        DialogResult = true;
    }

    private void RefreshHint()
    {
        if (HintText is null || ApplyHintButton is null)
            return;
        var hint = TvPower.Describe(_engine, ClockBox.Text, ThreadsBox.Text);
        _hintPercent = hint.Percent;
        HintText.Text = hint.Text;
        ApplyHintButton.IsEnabled = hint.Percent is not null;
    }
}
