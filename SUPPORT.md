# Setup and troubleshooting

## Widget reports that the Runtime is offline

1. Confirm Opulent Hardware Module Runtime is running in the Windows notification area.
2. In iCUE, open **Settings → Widget Access → BIO-GRID Audio Module**.
3. Confirm access to `localhost:17652` is enabled.
4. Close other companion applications that could be using the same local port.
5. Exit and restart the Runtime, then reload the widget.

## Start with Windows is checked but the Runtime does not launch

1. Confirm the executable remains in the same permanent folder from which it was registered.
2. Start the Runtime manually.
3. Right-click its notification-area icon.
4. Uncheck **Start with Windows**, then check it again.
5. Confirm the Runtime appears under **Windows Settings → Apps → Startup**.

Moving, renaming, replacing, or deleting the executable can invalidate its existing Windows startup path.

## Artwork disappears during natural track or video transitions

Use BIO-GRID Audio Module 1.1.1 with Runtime 1.0.2. This pairing contains both widget-side artwork preload/retry handling and Runtime-side recent-artwork caching.

Some media applications may not provide artwork to Windows. When no thumbnail is supplied, the widget displays its fallback media emblem.

