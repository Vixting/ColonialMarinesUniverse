using System.Collections.Generic;
using System.Linq;
using Content.Client._RMC14.UserInterface;
using Content.Client.CMU14.UI;
using Content.Client.Paper.UI;
using Content.Shared._RMC14.Chemistry.Reagent;
using Content.Shared.CMU14.Chemistry.Reagent;
using Content.Shared.CMU14.Chemistry.Reagents;
using Content.Shared.CMU14.Chemistry.Research;
using Content.Shared.Paper;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using static Content.Client.CMU14.Chemistry.Research.ResearchDataTerminalWindow;

namespace Content.Client.CMU14.Chemistry.Research;

[UsedImplicitly]
public sealed partial class ResearchDataTerminalBui : BoundUserInterface
{
    private const int XClearance = 6;
    private const string ReportPaperPrototype = "CMUWYPaper";

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototype = default!;

    private readonly RMCReagentSystem _reagent;

    private ResearchDataTerminalWindow? _window;
    private ResearchConfirmWindow? _confirm;
    private PaperWindow? _paper;
    private ResearchDataTerminalBuiState? _state;

    private enum SortColumn
    {
        Time,
        Compound,
    }

    private SortColumn _sort = SortColumn.Time;
    private bool _sortAscending;

    public ResearchDataTerminalBui(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
        IoCManager.InjectDependencies(this);
        _reagent = EntMan.System<RMCReagentSystem>();
    }

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<ResearchDataTerminalWindow>();

        _window.ReduceCooldownButton.OnPressed += _ => SendPredictedMessage(new CMUResearchReduceCooldownBuiMsg());
        _window.ReprintContractButton.OnPressed += _ => SendPredictedMessage(new ResearchDataTerminalPrintLastBuiMsg());
        _window.UpgradeButton.OnPressed += _ => OnUpgradePressed();
        _window.HideOldReportsButton.OnPressed += _ => Refresh();
        _window.DocumentsSortTimeButton.OnPressed += _ => SetSort(SortColumn.Time);
        _window.DocumentsSortCompoundButton.OnPressed += _ => SetSort(SortColumn.Compound);

        if (State is ResearchDataTerminalBuiState state)
            _state = state;

        Refresh();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is not ResearchDataTerminalBuiState s)
            return;

        _state = s;
        Refresh();
    }

    private void SetSort(SortColumn column)
    {
        if (_sort == column)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sort = column;
            _sortAscending = true;
        }

        Refresh();
    }

    private void Refresh()
    {
        if (_window is not { IsOpen: true } || _state is not { } state)
            return;

        var clearance = state.Clearance >= XClearance ? "X" : state.Clearance.ToString();
        _window.ClearanceLabel.Text = Loc.GetString("research-data-ui-clearance", ("NUM", clearance));
        _window.CreditsLabel.Text = Loc.GetString("research-data-ui-credits", ("NUM", state.Credits), ("DATA", state.SampleData));

        var cooldownActive = state.Picked && state.NextUpdate > _timing.CurTime;
        var canReduce = cooldownActive && state.Credits >= 1;
        _window.ReduceCooldownButton.Disabled = !canReduce;
        SetPurchasableStyle(_window.ReduceCooldownButton, canReduce, AmberWashed, _window.ReduceCooldownLabel);

        SetPurchasableStyle(_window.ReprintContractButton, true, label: _window.ReprintContractLabel);

        RefreshUpgrade(state);

        _window.CycleStart = state.LastTime;
        _window.CycleEnd = state.NextUpdate;

        PopulateContracts(state, cooldownActive);
        PopulateDocuments(state);
    }

    private void RefreshUpgrade(ResearchDataTerminalBuiState state)
    {
        var window = _window!;
        var xLocked = state.XLockedUntil is { } until && _timing.CurTime < until;

        bool enabled;
        if (state.Clearance >= XClearance)
        {
            window.UpgradeLabel.Text = Loc.GetString("research-data-ui-improve-maxed");
            enabled = false;
        }
        else if (xLocked)
        {
            var minutes = Math.Ceiling((state.XLockedUntil!.Value - _timing.CurTime).TotalMinutes);
            window.UpgradeLabel.Text = Loc.GetString("research-data-ui-improve-locked", ("TIME", minutes));
            enabled = false;
        }
        else
        {
            var key = state.Clearance == XClearance - 1 ? "research-data-ui-improve-x" : "research-data-ui-improve";
            window.UpgradeLabel.Text = Loc.GetString(key, ("NUM", state.UpgradeCost));
            enabled = state.Credits >= state.UpgradeCost;
        }

        window.UpgradeButton.Disabled = !enabled;
        SetPurchasableStyle(window.UpgradeButton, enabled, AmberWashed, window.UpgradeLabel);
    }

    private void OnUpgradePressed()
    {
        if (_state is not { } state)
            return;

        var toX = state.Clearance == XClearance - 1;
        OpenConfirmation(
            Loc.GetString(toX ? "research-data-ui-confirm-x-title" : "research-data-ui-confirm-improve-title"),
            Loc.GetString(toX ? "research-data-ui-confirm-x-text" : "research-data-ui-confirm-improve-text",
                ("NUM", state.UpgradeCost)),
            new ResearchDataTerminalAttemptUpgradeBuiMsg());
    }

    private void OpenConfirmation(string title, string text, BoundUserInterfaceMessage message)
    {
        _confirm?.Close();

        var confirm = new ResearchConfirmWindow();
        _confirm = confirm;
        confirm.Setup(title, text, Loc.GetString("research-data-ui-confirm"), Loc.GetString("research-data-ui-cancel"));
        confirm.AcceptButton.OnPressed += _ =>
        {
            SendPredictedMessage(message);
            confirm.Close();
        };
        confirm.DenyButton.OnPressed += _ => confirm.Close();
        confirm.OnClose += () =>
        {
            if (_confirm == confirm)
                _confirm = null;
        };
        confirm.OpenCentered();
    }

    private void PopulateContracts(ResearchDataTerminalBuiState state, bool cooldownActive)
    {
        var box = _window!.ContractsBox;
        _window.ContractsEmptyLabel.Visible = state.IDs.Count == 0;

        for (var i = 0; i < state.IDs.Count; i++)
        {
            var chem = state.IDs[i];

            ResearchContractCard card;
            if (i < box.ChildCount && box.GetChild(i) is ResearchContractCard existing)
            {
                card = existing;
            }
            else
            {
                card = new ResearchContractCard();
                card.OnTake = id => SendPredictedMessage(new ResearchDataTerminalPickChemBuiMsg(id));
                box.AddChild(card);
            }

            var difficulty = chem.GenTier switch
            {
                >= 3 => Loc.GetString("research-data-ui-diff-hard"),
                2 => Loc.GetString("research-data-ui-diff-inter"),
                _ => Loc.GetString("research-data-ui-diff-easy"),
            };

            card.ContractId = chem.ID;
            card.NameLabel.Text = chem.Name;
            card.DifficultyLabel.Text = Loc.GetString("research-data-ui-chem-difficulty", ("DIFF", difficulty));
            card.RecipeHintLabel.Text = Loc.GetString("research-data-ui-recipe-hint", ("NAME", GetReagentName(chem.RecipeHint)));
            card.PropertyHintLabel.Text = Loc.GetString("research-data-ui-property-hint", ("NAME", GetPropertyName(chem.PropertyHint)));
            card.SetTakeState(!cooldownActive, Loc.GetString("research-data-ui-chem-take"));
        }

        box.RemoveChildrenAfter(state.IDs.Count);
    }

    private void PopulateDocuments(ResearchDataTerminalBuiState state)
    {
        var window = _window!;
        window.SetSortIcon(window.DocumentsSortTimeButton,
            _sort == SortColumn.Time ? _sortAscending ? CMUIcons.ArrowDown19 : CMUIcons.ArrowDown91 : null);
        window.SetSortIcon(window.DocumentsSortCompoundButton,
            _sort == SortColumn.Compound ? _sortAscending ? CMUIcons.ArrowDownAZ : CMUIcons.ArrowDownZA : null);

        IEnumerable<KeyValuePair<int, (string, string, TimeSpan, bool, GeneratedReagentData, bool, bool)>> entries = state.Data;

        if (window.HideOldReportsButton.Pressed)
        {
            // Keep only the newest report per (simulation/analysis, compound).
            entries = entries
                .GroupBy(x => (x.Value.Item4, x.Value.Item5.Name))
                .Select(g => g.MaxBy(x => x.Key));
        }

        var ordered = _sort == SortColumn.Time
            ? entries.OrderBy(x => x.Value.Item3).ThenBy(x => x.Key)
            : entries.OrderBy(x => x.Value.Item5.Name.ToUpperInvariant()).ThenBy(x => x.Key);

        var list = (_sortAscending ? ordered : ordered.Reverse()).ToList();

        window.DocumentsEmptyLabel.Visible = list.Count == 0;

        var box = window.DocumentsBox;
        var index = 0;
        foreach (var (id, entry) in list)
        {
            ResearchDocumentRow row;
            if (index < box.ChildCount && box.GetChild(index) is ResearchDocumentRow existing)
            {
                row = existing;
            }
            else
            {
                row = new ResearchDocumentRow();
                row.OnRead = OpenReport;
                row.OnPrint = docId => SendPredictedMessage(new ResearchDataTerminalPrintChemBuiMsg(docId));
                box.AddChild(row);
            }

            row.Id = id;
            row.ScanTimeLabel.Text = entry.Item3.ToString(@"h\:mm\:ss");
            row.TypeLabel.Text = Loc.GetString(entry.Item4 ? "research-data-ui-analysis-sim" : "research-data-ui-analysis-scan");
            row.CompoundLabel.Text = entry.Item5.Name;

            index++;
        }

        box.RemoveChildrenAfter(index);
    }

    private void OpenReport(int id)
    {
        if (_state is not { } state || !state.Data.TryGetValue(id, out var entry))
            return;

        _paper?.Close();

        var paper = new PaperWindow();
        _paper = paper;

        if (_prototype.TryIndex(ReportPaperPrototype, out var proto) &&
            proto.TryComp(out PaperVisualsComponent? visuals, EntMan.ComponentFactory))
        {
            paper.InitVisuals(Owner, visuals);
        }

        paper.Populate(new PaperComponent.PaperBoundUserInterfaceState(entry.Item2, new List<StampDisplayInfo>()));
        paper.OnClose += () =>
        {
            if (_paper == paper)
                _paper = null;
        };
        paper.OpenCentered();
    }

    private string GetReagentName(string id)
    {
        return _reagent.TryIndex(id, out var reagent) ? reagent.LocalizedName : id;
    }

    private string GetPropertyName(string id)
    {
        return _prototype.TryIndex<ReagentPropertyPrototype>(id, out var property) ? property.LocalizedName : id;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;

        _confirm?.Close();
        _confirm = null;
        _paper?.Close();
        _paper = null;
    }
}
