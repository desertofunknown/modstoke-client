// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Sons.Ai;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientSpacecraftBody
{
    private GameObject? _navigationRoot;
    private readonly List<NavMeshCutSetup> _navigationCuts = new(6);
    private bool _navigationFailed;

    private void AttachNpcNavigation()
    {
        if (_navigationFailed) return;
        try
        {
            _navigationRoot = new GameObject("Spacecraft navigation");
            _navigationRoot.SetActive(false);
            _navigationRoot.transform.SetParent(Root.transform, false);
            // Follow the solid cabin, nose, wings and rear pods, leaving the central
            // aft entry and boarding ramp outside the navigation footprint.
            AddNavigationCut(new Vector3(0, 0, -2.55f), new Vector3(7.25f, 0, 13.3f));
            AddNavigationCut(new Vector3(0, 0, 7.9f), new Vector3(5.4f, 0, 7.6f));
            for (var side = -1; side <= 1; side += 2)
            {
                AddNavigationCut(new Vector3(side * 6.1f, 0, -3), new Vector3(4.8f, 0, 17));
                AddNavigationCut(new Vector3(side * 5.2f, 0, -13.6f), new Vector3(2.7f, 0, 3.8f));
            }
        }
        catch (Exception error) { FailNpcNavigation("initialization", error); }
    }

    private void AddNavigationCut(Vector3 center, Vector3 size)
    {
        var child = new GameObject("Hull navigation cut");
        child.SetActive(false);
        child.transform.SetParent(_navigationRoot!.transform, false);
        // Extend to the landing-foot plane so the ground graph below the hull
        // is cut without creating physical geometry across the cabin.
        const float top = 3.3f;
        const float bottom = FootY - .5f;
        center.y = (top + bottom) * .5f;
        size.y = top - bottom;
        var setup = NavMeshCutSetup.GetOrAddNavMeshCutSetup(child.transform);
        _navigationCuts.Add(setup);
        setup.SetCutEnabled(false);
        var cut = NavMeshCutSetup.GetOrAddNavMeshCut(child.transform);
        setup.CreateNavMeshCut(cut, size, center, 0, false);
        child.SetActive(true);
    }

    private void UpdateNpcNavigation()
    {
        if (_navigationFailed) return;
        try
        {
            if (_navigationRoot == null || !_navigationRoot) return;
            var enabled = Ready && _authoritative && _region == SpacecraftRegion.Island
                && _grounded && _parked && !_docked && _travel == SpacecraftTravel.None;
            if (_navigationRoot.activeSelf == enabled) return;
            if (!enabled) { DisableNpcNavigation(); return; }
            foreach (var setup in _navigationCuts) setup.SetCutEnabled(true);
            _navigationRoot.SetActive(true);
        }
        catch (Exception error) { FailNpcNavigation("update", error); }
    }

    private void DisableNpcNavigation()
    {
        if (_navigationFailed) return;
        try
        {
            if (_navigationRoot == null || !_navigationRoot || !_navigationRoot.activeSelf) return;
            _navigationRoot.SetActive(false);
            foreach (var setup in _navigationCuts)
                if (setup) setup.SetCutEnabled(false);
        }
        catch (Exception error) { FailNpcNavigation("deactivation", error); }
    }

    private void DisposeNpcNavigation()
    {
        var error = RemoveNpcNavigation();
        if (error != null) FailNpcNavigation("cleanup", error);
    }

    private void FailNpcNavigation(string operation, Exception error)
    {
        if (_navigationFailed) return;
        // Navigation is optional. Latch before cleanup so a failed native cut
        // cannot trigger reconstruction or another attempt on the next frame.
        _navigationFailed = true;
        var cleanupError = RemoveNpcNavigation();
        if (cleanupError != null) error = new AggregateException(error, cleanupError);
        Console.WriteLine("Spacecraft NPC navigation disabled after " + operation + " failed: " + error);
    }

    private Exception? RemoveNpcNavigation()
    {
        var root = _navigationRoot;
        _navigationRoot = null;
        List<Exception>? failures = null;
        try
        {
            if (root) root!.SetActive(false);
        }
        catch (Exception error) { (failures ??= new()).Add(error); }
        foreach (var setup in _navigationCuts)
        {
            try
            {
                if (setup) setup.SetCutEnabled(false);
            }
            catch (Exception error) { (failures ??= new()).Add(error); }
        }
        _navigationCuts.Clear();
        try
        {
            // Setup and cut share the child: removing setup alone leaves the
            // native clipper alive, so destroy the entire owned hierarchy.
            if (root) UnityEngine.Object.Destroy(root);
        }
        catch (Exception error) { (failures ??= new()).Add(error); }
        return failures == null ? null : new AggregateException("Spacecraft navigation cleanup failed.", failures);
    }
}
