namespace SensorDeck.App.Services;

/// <summary>
/// Points everything that used a sensor id at another one — dashboard tiles, alert rules, automation sensor rules,
/// calculated-sensor inputs, the History page and the sensor's rename/correction — when hardware changed and the old
/// sensor is gone.
/// </summary>
public sealed class SensorLinker
{
    private readonly SensorCustomizationService _customizations;
    private readonly AppSettings _settings;

    public SensorLinker(SensorCustomizationService customizations, AppSettings settings)
    {
        _customizations = customizations;
        _settings = settings;
    }

    /// <summary>Raised after the settings-level changes, so dashboards and alerts can update (old id, new id).</summary>
    public event Action<string, string>? Relinked;

    public void Relink(string oldId, string newId)
    {
        if (oldId == newId)
        {
            return;
        }

        _customizations.Move(oldId, newId);

        // Alert rule: carry the user's limits over unless the new sensor already has its own.
        var rule = _settings.AlertRules.FirstOrDefault(r => r.SensorId == oldId);
        if (rule is not null)
        {
            if (_settings.AlertRules.Any(r => r.SensorId == newId))
            {
                _settings.AlertRules.Remove(rule);
            }
            else
            {
                rule.SensorId = newId;
            }
        }

        var index = _settings.HistorySensors.IndexOf(oldId);
        if (index >= 0)
        {
            if (_settings.HistorySensors.Contains(newId))
            {
                _settings.HistorySensors.RemoveAt(index);
            }
            else
            {
                _settings.HistorySensors[index] = newId;
            }
        }

        // Automation sensor rules and calculated-sensor inputs are read straight from settings on every check/frame
        // (the automation engine restarts a rule's timers when its SensorId changes).
        foreach (var automation in _settings.Automation.Rules.Where(r => r.SensorId == oldId))
        {
            automation.SensorId = newId;
        }

        foreach (var calc in _settings.CalculatedSensors)
        {
            for (var i = 0; i < calc.Inputs.Count; i++)
            {
                if (calc.Inputs[i] == oldId)
                {
                    calc.Inputs[i] = newId;
                }
            }
        }

        _settings.Save();
        Relinked?.Invoke(oldId, newId);
    }
}
