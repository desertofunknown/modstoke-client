// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using Sons.Items.Core;
using UnityEngine;

namespace Modstoke.Client;

internal sealed partial class ClientTabletPresence
{
    private sealed partial class RemotePose
    {
        private void HideItems()
        {
            var items = _setup._equippedItemInstances;
            if (items == null || items.WasCollected || items.Count > 32) return;
            var props = _setup._inventoryProps;
            var objects = Live(props) ? props._gameObjectInstances : null;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || item.WasCollected) continue;
                var itemObject = item._ItemObject_k__BackingField;
                var root = itemObject;
                // Native equip publishes the held root before its async renderable
                // callback assigns ItemObject and _itemRenderable.
                if (objects != null && !objects.WasCollected && objects.Count <= 32
                    && objects.TryGetValue(item._itemID, out var heldRoot) && Live(heldRoot)) root = heldRoot;
                HideItemRenderers(root);
                if (!Same(root, itemObject)) HideItemRenderers(itemObject);
                var renderable = item._itemRenderable;
                if (!Live(renderable) && Live(root)) renderable = root.GetComponentInChildren<ItemRenderable>(true);
                // The addressable visual can be parented beside the item object.
                // Hide its renderers without disabling the owner and unloading it.
                if (Live(renderable)) HideItemRenderers(renderable._cachedLoadedObject);
            }
        }

        private void HideItemRenderers(GameObject? root)
        {
            if (!Live(root) || !root.transform.IsChildOf(_entity.transform)
                || _animator.transform.IsChildOf(root.transform)) return;
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.WasCollected || renderers.Length > 32) return;
            foreach (var renderer in renderers)
            {
                if (!Live(renderer) || renderer.forceRenderingOff) continue;
                _hiddenItems.Add(renderer);
                // Leave native equip/unequip visibility state under native ownership.
                renderer.forceRenderingOff = true;
            }
        }
    }
}
