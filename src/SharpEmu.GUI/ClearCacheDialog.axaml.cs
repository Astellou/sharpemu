// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia.Controls;

namespace SharpEmu.GUI;

// Asks before a game's caches are deleted; closes with true when confirmed.
public partial class ClearCacheDialog : Window
{
    public ClearCacheDialog()
        : this(string.Empty, string.Empty)
    {
    }

    public ClearCacheDialog(string gameName, string size)
    {
        InitializeComponent();
        MessageText.Text = Localization.Instance.Format("Library.ClearCache.Message", gameName, size);
        CancelButton.Click += (_, _) => Close(false);
        ClearButton.Click += (_, _) => Close(true);
    }
}
