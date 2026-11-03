// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Globalization;
using Modstoke.Sons;
using TMPro;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientTerminalView
{
    private RectTransform _clockConditions = null!;
    private ButtonWidget _clockConditionsTab = null!;
    private TextMeshProUGUI _environmentSeason = null!, _environmentSeasonDetail = null!;
    private TextMeshProUGUI _environmentWeather = null!, _environmentWeatherDetail = null!;
    private TextMeshProUGUI _environmentClock = null!, _environmentClockDetail = null!;
    private TextMeshProUGUI _environmentAvailability = null!;

    private void BuildClockEnvironment()
    {
        _clockConditionsTab = Button(_watchSurface, "ConditionsTab", "Conditions", 472f, 0f, 220f, 52f,
            () => SelectClockPage(2));
        _clockConditions = Node(_watchSurface, "Conditions");
        Place(_clockConditions, 0f, 68f, 1328f, 532f);

        var season = RoundedPanel(_clockConditions, "Season", Surface, 0f, 0f, 644f, 254f);
        Text(season.transform, "Heading", "SEASON", 20f, Mint, 28f, 20f, 588f, 30f, true);
        _environmentSeason = Text(season.transform, "Reading", "Season unavailable", 36f, Ink, 28f, 68f, 588f, 52f, true);
        _environmentSeasonDetail = Text(season.transform, "Detail", "", 24f, Muted, 28f, 140f, 588f, 86f);

        var weather = RoundedPanel(_clockConditions, "Weather", Surface, 668f, 0f, 660f, 254f);
        Text(weather.transform, "Heading", "WEATHER", 20f, Mint, 28f, 20f, 604f, 30f, true);
        _environmentWeather = Text(weather.transform, "Reading", "Weather unavailable", 34f, Ink, 28f, 68f, 604f, 52f, true);
        _environmentWeatherDetail = Text(weather.transform, "Detail", "", 24f, Muted, 28f, 140f, 604f, 86f);

        var clock = RoundedPanel(_clockConditions, "IslandClock", Surface, 0f, 278f, 1328f, 194f);
        Text(clock.transform, "Heading", "ISLAND CLOCK", 20f, Mint, 28f, 20f, 604f, 30f, true);
        _environmentClock = Text(clock.transform, "Reading", "Island time unavailable", 36f, Ink, 28f, 78f, 604f, 74f, true);
        _environmentClockDetail = Text(clock.transform, "Detail", "", 24f, Muted, 696f, 64f, 604f, 100f);
        _environmentAvailability = Text(_clockConditions, "Availability", "Waiting for current world readings.",
            21f, Muted, 24f, 490f, 1280f, 34f);

        foreach (var text in new[] { _environmentSeason, _environmentSeasonDetail, _environmentWeather,
            _environmentWeatherDetail, _environmentClock, _environmentClockDetail, _environmentAvailability })
        {
            text.richText = false;
            text.overflowMode = TextOverflowModes.Ellipsis;
        }
        _environmentSeason.enableWordWrapping = false;
        _environmentWeather.enableWordWrapping = false;
        _environmentClock.enableWordWrapping = false;
        _environmentAvailability.enableWordWrapping = false;
        SetEnvironment(ClientEnvironmentSnapshot.Empty);
    }

    internal void SetEnvironment(ClientEnvironmentSnapshot snapshot)
    {
        if (_disposed || _environmentAvailability == null) return;
        if (snapshot.Season is { } season)
        {
            _environmentSeason.text = season.ActiveSeason?.ToString() ?? "Season unknown";
            _environmentSeasonDetail.text = (season.ActiveSeason.HasValue ? "Current local season"
                : "Unrecognized season (" + season.RawActiveSeason.ToString(CultureInfo.InvariantCulture) + ")")
                + (season.IsSeasonLocked ? "\nSeason setting is locked" : "\nSeason setting is unlocked");
        }
        else
        {
            _environmentSeason.text = "Season unavailable";
            _environmentSeasonDetail.text = EnvironmentReadMessage(snapshot.SeasonStatus, "season");
        }
        if (snapshot.Weather is { } weather)
        {
            _environmentWeather.text = weather.Precipitation switch
            {
                Precipitation.None => "No precipitation",
                Precipitation.Light => "Light precipitation",
                Precipitation.Medium => "Moderate precipitation",
                Precipitation.Heavy => "Heavy precipitation",
                _ => "Precipitation unknown"
            };
            var clouds = weather.CloudState switch
            {
                CloudState.Idle => "Cloud phase idle",
                CloudState.GrowingClouds => "Clouds building",
                CloudState.ReducingClouds => "Clouds clearing",
                CloudState.Raining => "Precipitation phase",
                _ => "Cloud phase unknown (" + weather.RawCloudState.ToString(CultureInfo.InvariantCulture) + ")"
            };
            _environmentWeatherDetail.text = clouds + " · " + (weather.IsSnowing ? "Snow active" : "No snow reported")
                + (weather.IsRainBlocked ? "\nRain is currently blocked"
                    : !weather.Precipitation.HasValue ? "\nUnrecognized precipitation ("
                        + weather.RawPrecipitation.ToString(CultureInfo.InvariantCulture) + ")" : "\nObserved locally");
        }
        else
        {
            _environmentWeather.text = "Weather unavailable";
            _environmentWeatherDetail.text = EnvironmentReadMessage(snapshot.WeatherStatus, "weather");
        }
        if (snapshot.Clock is { } clock)
        {
            var island = ClientIslandClock.FromSnapshot(clock);
            _environmentClock.text = island.DayText + " · " + island.TimeText;
            _environmentClockDetail.text = clock.IsOverrideActive
                ? "Time override active\nWorld clock " + ClockTime(clock.GameTime)
                : "No time override\nObserved locally";
        }
        else
        {
            _environmentClock.text = "Island time unavailable";
            _environmentClockDetail.text = EnvironmentReadMessage(snapshot.ClockStatus, "island clock");
        }
        _environmentSeason.color = snapshot.Season == null ? Muted : Ink;
        _environmentWeather.color = snapshot.Weather == null ? Muted : Ink;
        _environmentClock.color = snapshot.Clock == null ? Muted : Ink;
        var available = (snapshot.Clock == null ? 0 : 1) + (snapshot.Weather == null ? 0 : 1) + (snapshot.Season == null ? 0 : 1);
        var age = Math.Max(0, Environment.TickCount64 - snapshot.SampledAt) / 1000;
        _environmentAvailability.text = snapshot.SampledAt == 0 ? "Waiting for current world readings."
            : (available == 3 ? "Local readings" : available.ToString(CultureInfo.InvariantCulture) + " of 3 local readings available")
                + " · " + (age == 0 ? "Updated just now" : "Updated " + age.ToString(CultureInfo.InvariantCulture) + "s ago");
        _environmentAvailability.color = snapshot.SampledAt != 0 && available < 3 ? Amber : Muted;
    }

    private static string ClockTime(TimeSpan time) => time.Hours.ToString("00", CultureInfo.InvariantCulture)
        + ":" + time.Minutes.ToString("00", CultureInfo.InvariantCulture);

    private static string EnvironmentReadMessage(EnvironmentReadStatus status, string subject) => status switch
    {
        EnvironmentReadStatus.ClockUnavailable or EnvironmentReadStatus.WeatherUnavailable
            or EnvironmentReadStatus.SeasonUnavailable => "Waiting for the " + subject + ".",
        EnvironmentReadStatus.Failed => "This reading could not be refreshed.\nIt will update when available.",
        _ => "Waiting for a current local reading."
    };
}
