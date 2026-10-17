#!/bin/bash
# Builds DiscordAccess.app under macos/build/apps.noindex/<time>/ (design 6, stage 2d) by default; Spotlight and the
# app list skip a folder named .noindex, so build copies are not offered beside the installed app. Set
# DISCORDACCESS_BUILD_OUTPUT_ROOT to use a different output directory, such as a local temporary folder when a file
# provider adds metadata to bundles inside the checkout.
#
# The pinned engine (macos/build/engine/sing-box, from build-engine.py) goes in unchanged: re-signing it would change the
# SHA-256 and cdhash the app pins, and a personal certificate would make them differ on every Mac. The app's signature
# seals it with everything else. The app is signed with DISCORDACCESS_SIGN_IDENTITY (a code-signing identity in the
# keychain); without one it is signed ad hoc, and macOS then forgets its Full Disk Access with every build.
#
# Earlier builds are never deleted: macOS scans a newly run executable, and deleting one mid-scan crashed its scanner.
set -euo pipefail

macos="$(cd "$(dirname "$0")/.." && pwd)"
repo="$(dirname "$macos")"
identity="${DISCORDACCESS_SIGN_IDENTITY:--}"
engine="$macos/build/engine/sing-box"
hook="$repo/bridge/attachment-download.cjs"
agent="io.github.alpertknn.DiscordAccess.agent.plist"

fail() {
    echo "build-app: $1" >&2
    exit 1
}

[ -x "$engine" ] || fail "Engine not found: $engine. Build it first with macos/scripts/build-engine.py."
[ -f "$hook" ] || fail "Attachment download hook not found: $hook."

swift build --package-path "$macos" -c release --product DiscordAccess
binary="$(swift build --package-path "$macos" -c release --show-bin-path)/DiscordAccess"
[ -x "$binary" ] || fail "Built app not found: $binary."

output_root="${DISCORDACCESS_BUILD_OUTPUT_ROOT:-$macos/build/apps.noindex}"
mkdir -p "$output_root"
output="$output_root/$(date -u +%Y%m%d-%H%M%S)"
app="$output/DiscordAccess.app"
[ ! -e "$output" ] || fail "Output folder already exists: $output. Try again in a second."
mkdir -p "$app/Contents/MacOS" "$app/Contents/Helpers" "$app/Contents/Resources" "$app/Contents/Library/LaunchAgents"
install -m 0644 "$macos/Resources/Info.plist" "$app/Contents/Info.plist"
install -m 0644 "$macos/Resources/$agent" "$app/Contents/Library/LaunchAgents/$agent"
# Render all icon sizes from the same AppKit vector used by the menu bar.
xcrun swiftc "$macos/Sources/DiscordAccess/BrandIcon.swift" "$macos/scripts/render-icons.swift" -o "$output/render-icons"
"$output/render-icons" "$output/AppIcon.iconset" "$app/Contents/Resources/AppIcon.icns"
install -m 0644 "$hook" "$app/Contents/Resources/attachment-download.cjs"
install -m 0755 "$binary" "$app/Contents/MacOS/DiscordAccess"
# cp keeps the engine's bytes, and so its linker signature, exactly.
cp "$engine" "$app/Contents/Helpers/sing-box"
chmod 0755 "$app/Contents/Helpers/sing-box"

if [ "$identity" = "-" ]; then
    echo "build-app: No signing identity given; signing the app ad hoc. Full Disk Access must be granted again after every new build." >&2
fi
codesign --force --sign "$identity" --options runtime --timestamp=none "$app/Contents/MacOS/DiscordAccess"
codesign --force --sign "$identity" --options runtime --timestamp=none "$app"
codesign --verify --strict --deep --verbose=2 "$app"

# The app checks its own engine and hook against the digests it pins.
"$app/Contents/MacOS/DiscordAccess" --verify-package

echo "$app"
