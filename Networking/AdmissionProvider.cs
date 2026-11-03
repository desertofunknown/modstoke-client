// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Modstoke.Loader;
using Modstoke.Sons;

namespace Modstoke.Client;

internal sealed class ClientAdmissionProvider : IMenuContentProvider, IConfigProvider, ICommandProvider, IMenuProviderPresentation
{
    internal const string MenuId = "modstoke.client";
    internal const string SettingsId = MenuId + ".settings";
    private const string AdmissionKey = "EnableManagedAdmission";
    private const string PreviousPageCommand = "requirements.previous";
    private const string NextPageCommand = "requirements.next";
    private const int RequirementsPerPage = 8;
    private readonly object _sync = new();
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly ClientConfiguration _configuration;
    private bool _configurationReady;
    private string _configurationStatus;
    private bool _active = true;
    private bool _started;
    private bool _enableManagedAdmission;
    private bool _pending;
    private bool _restartRequired;
    private bool _superseded;
    private Guid? _ownedRevision;
    private double _nextObservation;
    private ModAdmissionSelectionSnapshot? _selection;
    private ControlResult _readResult = ControlResult.Unavailable;
    private string _status = "Waiting for configuration.";
    private ModAdmissionObservation? _latestAttempt;
    private ModAdmissionReadStatus _recentReadResult = ModAdmissionReadStatus.Unavailable;
    private int _requirementsPage;

    internal ClientAdmissionProvider()
    {
        _configuration = new ClientConfiguration(GameRuntime.Environment.GameRootDirectory);
        _configurationReady = _configuration.TryLoad(out _enableManagedAdmission, out _configurationStatus);
        if (!_configurationReady)
            _status = "Admission selection is blocked until configuration is corrected explicitly.";
    }

    public string Id => MenuId;
    string IConfigProvider.Id => SettingsId;
    public string DisplayName => "Server compatibility";
    string IConfigProvider.DisplayName => "Modstoke Client Settings";
    public string Kind => MenuProviderKinds.Status;
    public int Priority => 500;
    public string Source => "Modstoke Client by OpenACAI";
    public string Description => "Optional native package compatibility at connection admission";
    public bool RequiresAuthorization => false;
    public string OwnerModId => "modstoke.client";
    public bool Enabled { get { lock (_sync) return _active; } }

    internal void Start()
    {
        lock (_sync)
        {
            _started = true;
            _pending = _configurationReady;
        }
        Update();
    }

    internal void Update()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            return;
        lock (_sync)
        {
            if (!_active || !_started)
                return;
            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            if (!_pending && now < _nextObservation)
                return;
            _nextObservation = now + 0.5;
            ObserveRecentAttempt();
            Observe();
            if (!_pending)
                return;

            // Each configuration change gets one attempt; observation never retries selection.
            _pending = false;
            _restartRequired = false;
            if (_readResult != ControlResult.Success || _selection == null)
            {
                _status = "Selection could not be read: " + _readResult + ". Change the setting again or restart to retry.";
                _restartRequired = _enableManagedAdmission || _ownedRevision.HasValue;
                return;
            }

            if (_selection.ServerRequirements != null)
            {
                _status = "A server integration controls admission. Its requirements and selection are preserved.";
                return;
            }
            if (_enableManagedAdmission && _selection.Selected)
            {
                if (!_selection.GameplayGateSelected || _selection.TransportState == ControlState.Disabled
                    || _selection.TransportState == ControlState.Failed)
                {
                    _restartRequired = true;
                    _status = "Admission is selected, but the gate or transport is unavailable. Restart the game before connecting.";
                }
                else
                    _status = OwnsSelection ? "Client admission selected." : "Client admission is selected by another integration.";
                return;
            }
            if (!_enableManagedAdmission && !OwnsSelection)
            {
                _status = "Client participation is disabled. Selections belonging to other integrations are preserved.";
                return;
            }
            if (!_selection.CanChangeForNextStart)
            {
                _restartRequired = true;
                _status = "Configuration changed while networking is starting, running, or shutting down. Restart the game to apply it.";
                return;
            }

            var result = ModAdmission.SelectForNextStart(_enableManagedAdmission, null);
            if (result != ControlResult.Success)
            {
                _restartRequired = true;
                _status = "Selection was not changed: " + result + ". Change the setting again or restart to retry.";
                return;
            }

            // SDK calls and the matching revision read run consecutively on the owner thread.
            _ownedRevision = null;
            _superseded = false;
            Observe();
            if (_readResult != ControlResult.Success || _selection == null)
            {
                _status = "Selection changed, but its revision could not be read. Ownership is unknown; no selection will be cleared.";
                return;
            }
            if (_selection.Selected != _enableManagedAdmission || (_enableManagedAdmission
                && (!_selection.GameplayGateSelected || _selection.ServerRequirements != null)))
            {
                _restartRequired = true;
                _status = "Selection changed, but the expected client mode could not be confirmed. No selection will be cleared.";
                return;
            }
            if (_enableManagedAdmission)
                _ownedRevision = _selection.Revision;
            _status = _enableManagedAdmission ? "Client admission selected for the next connection." : "The selection owned by Modstoke Client was cleared.";
        }
    }

    private bool OwnsSelection => _selection != null && _ownedRevision.HasValue
        && _selection.Revision == _ownedRevision.Value;

    private void Observe()
    {
        _readResult = ModAdmission.TryReadSelection(out var selection);
        _selection = _readResult == ControlResult.Success ? selection : null;
        if (_selection != null && _ownedRevision.HasValue && !OwnsSelection)
        {
            _ownedRevision = null;
            _superseded = true;
            _status = "Another integration superseded the client selection. Modstoke Client will not restore or clear it automatically.";
        }
    }

    private void ObserveRecentAttempt()
    {
        _recentReadResult = ModAdmission.TryReadRecent(out var observations);
        ModAdmissionObservation? latest = null;
        if (_recentReadResult == ModAdmissionReadStatus.Success)
        {
            // SDK order is attempt creation order, not the last update timestamp.
            foreach (var observation in observations)
            {
                if (!observation.IsJoiningClient)
                    continue;
                latest = observation;
                break;
            }
        }
        if (latest?.PeerId != _latestAttempt?.PeerId)
            _requirementsPage = 0;
        // A failed read clears stale data; menu and command callbacks never read the SDK.
        _latestAttempt = latest;
        _requirementsPage = Math.Min(_requirementsPage, RequirementsPageCount - 1);
    }

    private int RequirementsPageCount => Math.Max(1,
        ((_latestAttempt?.Requirements.Count ?? 0) + RequirementsPerPage - 1) / RequirementsPerPage);

    public IReadOnlyList<CommandSnapshot> GetCommands()
    {
        lock (_sync)
            return new[] {
                new CommandSnapshot(PreviousPageCommand, "Previous requirements", "Show the previous eight requirements.",
                    "Page " + (_requirementsPage + 1) + " of " + RequirementsPageCount,
                    _active && _latestAttempt != null && _requirementsPage > 0, false),
                new CommandSnapshot(NextPageCommand, "Next requirements", "Show the next eight requirements.",
                    "Page " + (_requirementsPage + 1) + " of " + RequirementsPageCount,
                    _active && _latestAttempt != null && _requirementsPage + 1 < RequirementsPageCount, false)
            };
    }

    public bool TryInvokeCommand(string commandId, out CommandResultSnapshot result)
    {
        lock (_sync)
        {
            var nextPage = commandId == PreviousPageCommand ? _requirementsPage - 1
                : commandId == NextPageCommand ? _requirementsPage + 1 : -1;
            if (!_active || _latestAttempt == null || nextPage < 0 || nextPage >= RequirementsPageCount)
            {
                result = CommandResultSnapshot.Failure("That requirements page is unavailable.");
                return false;
            }
            _requirementsPage = nextPage;
            result = CommandResultSnapshot.Success("Showing requirements page " + (nextPage + 1) + " of " + RequirementsPageCount + ".");
            return true;
        }
    }

    public IReadOnlyList<ConfigValueSnapshot> GetSettings()
    {
        lock (_sync)
            return new[] { new ConfigValueSnapshot("Admission", AdmissionKey, "Enable managed admission",
                ConfigValueKinds.Boolean, _enableManagedAdmission.ToString(), "False",
                "Opt in to exact required native mod ID/version compatibility. Changes during networking require a game restart.",
                canEdit: _active, requiresRestart: _selection == null || !_selection.CanChangeForNextStart) };
    }

    public bool TrySetValue(string key, string value, out string status)
    {
        lock (_sync)
        {
            if (!_active)
            {
                status = "Modstoke Client has stopped.";
                return false;
            }
            if (!string.Equals(key, AdmissionKey, StringComparison.Ordinal) || !bool.TryParse(value, out var enabled))
            {
                status = "EnableManagedAdmission requires a Boolean value.";
                return false;
            }
            if (!_configuration.TrySave(enabled, out status))
            {
                _configurationStatus = status;
                return false;
            }
            _configurationReady = true;
            _configurationStatus = status;
            _enableManagedAdmission = enabled;
            _pending = true;
            status = "Configuration saved. The game thread will check whether it can be applied before networking starts.";
            return true;
        }
    }

    public MenuContentSnapshot CreateContent()
    {
        lock (_sync)
        {
            var selection = _selection;
            var mode = selection == null ? "Unavailable: " + _readResult
                : !selection.Selected ? "Not selected"
                : selection.ServerRequirements != null ? "Server requirements selected" : "Client admission selected";
            var ownership = selection == null ? "Unknown" : OwnsSelection ? "Modstoke Client" : _superseded ? "Superseded by another integration"
                : selection?.Selected == true ? "Another integration" : "None";
            return new MenuContentSnapshot(DisplayName, "by OpenACAI | Last game-thread observations",
                new[] { CreateAttemptSection(), new MenuSectionSnapshot("Admission configuration and selection", new[] {
                    Row("Configured participation", !_configurationReady ? "Unavailable; selection blocked"
                        : _enableManagedAdmission ? "Enabled" : "Disabled (default)"),
                    Row("Configuration", _configurationStatus),
                    Row("SDK selection", mode),
                    Row("Selection ownership", ownership),
                    Row("Gameplay gate selected", selection?.GameplayGateSelected.ToString() ?? "Unknown"),
                    Row("Transport", selection?.TransportState.ToString() ?? "Unknown"),
                    Row("Can change before next start", selection?.CanChangeForNextStart.ToString() ?? "Unknown"),
                    Row("Restart required", _restartRequired ? "Yes" : _pending ? "Awaiting game-thread check" : "No pending restart"),
                    Row("Last selection action", _status),
                    Row("Scope", "Exact required native mod IDs and versions; reports are untrusted client claims."),
                    Row("Limits", "Selection is not connection admission, integrity proof, or combat authorization.")
                }) });
        }
    }

    private MenuSectionSnapshot CreateAttemptSection()
    {
        var rows = new List<MenuRowSnapshot>();
        var attempt = _latestAttempt;
        if (attempt == null)
        {
            rows.Add(Row("Admission observation", _recentReadResult == ModAdmissionReadStatus.Success
                ? "No joining-client admission decision recorded in retained history."
                : "Unavailable: " + _recentReadResult + ". Previous cached results were cleared."));
            rows.Add(Row("Meaning", "Enrollment can fail before native package admission begins; no acceptance or rejection can be inferred."));
        }
        else
        {
            rows.Add(Row("Record", attempt.ConnectionEnded
                ? "Historical attempt; connection ended. The last observed state is retained."
                : "Not observed ended; this does not confirm a live connection."));
            rows.Add(Row("Last observed state", attempt.State switch {
                ModAdmissionState.Pending => attempt.ConnectionEnded
                    ? "Pending when the connection ended; no final admission decision was recorded."
                    : "Pending: waiting for native package admission.",
                ModAdmissionState.Admitted when attempt.EndReason == "HostNotParticipating"
                    => "This server does not use Modstoke admission.",
                ModAdmissionState.Admitted => "Admitted: local compatibility acceptance observed.",
                ModAdmissionState.Rejected => "Rejected: " + RejectionDescription(attempt.EndReason),
                _ => "Unknown admission state."
            }));
            rows.Add(Row("Last observed (UTC)", attempt.ObservedAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)));
            rows.Add(Row("Requirements name", attempt.RequirementsName.Length == 0
                ? "Not recorded" : DisplayText(attempt.RequirementsName, 120)));
            var requirements = attempt.Requirements;
            if (requirements.Count == 0)
                rows.Add(Row("Requirement comparison", "No per-package comparison was recorded."));
            else
            {
                var first = _requirementsPage * RequirementsPerPage;
                var end = Math.Min(first + RequirementsPerPage, requirements.Count);
                rows.Add(Row("Requirement comparison", "Page " + (_requirementsPage + 1) + " of " + RequirementsPageCount
                    + " | " + (first + 1) + "-" + end + " of " + requirements.Count));
                for (var index = first; index < end; index++)
                {
                    var requirement = requirements[index];
                    rows.Add(new MenuRowSnapshot(DisplayText(requirement.Id, 256),
                        "Required exact version: " + DisplayText(requirement.RequiredVersion, 128),
                        RequirementDescription(requirement.Status), ""));
                }
            }
        }
        rows.Add(Row("Limits", "Local compatibility observations are not integrity proof, connection status, or gameplay authorization."));
        rows.Add(Row("History", "Most recent retained joining attempt only; history is bounded and clears when the game process restarts."));
        return new MenuSectionSnapshot("Latest joining-client admission", rows);
    }

    private static string RejectionDescription(string reason) => reason switch {
        "IncompatibleRequirements" => "Required native packages did not match.",
        "InvalidMessage" => "The admission exchange contained an invalid message.",
        "Unavailable" => "Native package admission was unavailable.",
        "Expired" => "The admission exchange timed out.",
        "LocalModsChanged" => "Native package state changed during admission.",
        _ => "No recognized rejection reason was recorded."
    };

    private static string RequirementDescription(NativeRequirementStatus status) => status switch {
        NativeRequirementStatus.Matched => "Matched",
        NativeRequirementStatus.Missing => "Missing",
        NativeRequirementStatus.VersionMismatch => "Version mismatch",
        NativeRequirementStatus.Inactive => "Inactive",
        NativeRequirementStatus.Ambiguous => "Ambiguous package ID",
        NativeRequirementStatus.Unavailable => "Comparison unavailable",
        NativeRequirementStatus.InvalidRequirement => "Invalid requirement",
        _ => "Unknown comparison status"
    };

    private static string DisplayText(string value, int maximumScalars)
    {
        // Server-supplied text must not introduce rich-text tags or invisible layout controls.
        var text = new StringBuilder();
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (count++ == maximumScalars)
            {
                text.Append('…');
                break;
            }
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                text.Append(' ');
            else if (rune.Value == '<')
                text.Append('‹');
            else if (rune.Value == '>')
                text.Append('›');
            else
                text.Append(rune.ToString());
        }
        return text.ToString();
    }

    internal void Deactivate()
    {
        lock (_sync)
        {
            _active = false;
            _pending = false;
        }
    }

    private static MenuRowSnapshot Row(string label, string value) => new(label, value, "", "");
}
