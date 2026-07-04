# MediaDebrid-cli Architecture & Internal Documentation

This document provides a deep-dive into the custom engineering and architectural decisions that make `MediaDebrid-cli` a robust and high-performance media downloader.

## 1. Custom TUI Framework (`Tui/TuiApp.cs`)

The application utilizes `Spectre.Console` for its rich Terminal User Interface (TUI), but extends it significantly with custom components to handle high-volume data and complex input scenarios.

### 1.1 Typewriter Input Reader (`ReadLineWithEffectAsync` in `Tui/Components.cs`)
To solve standard terminal wrapping bugs where the cursor gets "stuck" at the left edge when backspacing long strings, we implemented a custom key-listening loop.

- **Mechanism**: Uses `Console.ReadKey(intercept: true)` to process raw keystrokes.
- **Smart Wrap-Around**: Detects when `Console.CursorLeft == 0` during a Backspace. It manually calculates the terminal width and jumps the cursor to `Row - 1, Column Max` to simulate natural multi-line editing.
- **Typewriter Effect**: When pasting large strings (like magnet links), the method processes the buffer with a **Batch Delay** (triggering a 1ms `Task.Delay` every 5 characters). This ensures a visible "filling in" animation without making the process too slow for long links.
- **Features**: Supports `secret` masking for API keys and `defaultValue` pre-filling for path prompts.

### 1.2 Custom Progress Columns
Standard progress bars lack the domain-specific data needed for media downloading. We implemented several `ProgressColumn` extensions:

- **`SpinnerColumn`**: A centralized, state-aware spinner. It switches between the "Arc" animation (active), a green checkmark `✓` (finished), a blue spinner (saved/paused), and a red `X` (cancelled/failed).
- **`EpisodeColumn`**: Dynamically displays TV show metadata (e.g., `S01E05`) next to the progress bar. In multi-season downloads, this column extracts the correct season and episode number from the specific file being downloaded for accurate real-time feedback.
- **`CustomTransferSpeedColumn` & `CustomEtaColumn`**: High-precision calculation columns that pull from a `ConcurrentDictionary` of real-time speeds provided by the `Downloader`.

### 1.3 Execution Modes
`TuiApp` handles multiple operational modes routed from the CLI:
- **Interactive Download**: Prompts for magnets, handles season/episode selection, and downloads the files.
- **Unrestricted Link Generation (`unres`)**: Follows the same selection flow but bypasses the downloader entirely, instead outputting direct download URLs to the terminal for external use.

### 1.4 Stateful Spacing Engine (`Tui/PrintGap.cs`)
To eliminate manual layout flags (e.g., `needsNewline`) and prevent ugly double-spacing (negative space) in terminal layouts, we built a modular, state-tracking spacing engine.

- **Mechanism**: Maintains an in-memory `_hasGap` flag.
- **`Print()`**: Dynamically prints a single empty line gap only if the previous line wasn't already a gap.
- **`Suppress()`**: Sets the state to `true` to force-bypass subsequent spacing requests. This is called immediately before and after padded live components (like Spectre's `AnsiConsole.Progress` bars) to perfectly preserve tight visual ratios.
- **State Resetting**: All wrapped printing methods (`Markup`, `MarkupLine`, `Write`) automatically reset `_hasGap = false` to indicate that content has been printed, indicating a separator gap is now permitted.
- **Prompt Synchronization**: Seamlessly coordinates with intercepted custom inputs (like `ReadLineWithEffectAsync`) to avoid shifting active status spinners while ensuring the gap printer remains correctly primed.

---

## 2. Resumable Download Engine (`Features/Download_Manager/Downloader.cs`)

The core of the application is a high-performance, segmented downloader designed to survive application restarts and network failures.

### 2.1 The 4KB Metadata Footer
Unlike traditional downloaders that use sidecar files (e.g., `.part` files), `MediaDebrid-cli` uses a **Binary Footer** system.

- **Storage**: A fixed 4096-byte (4KB) area is reserved at the absolute end of every `.mdebrid` temporary file.
- **Magic Marker**: The last 8 bytes are the ASCII string `MDEBRID!`. This allows the app to instantly verify if a file was created by this application.
- **JSON Metadata**: The area preceding the marker stores a `ResumeMetadata` object containing:
  - Original Magnet URI.
  - File ID on the debrid service.
  - Total file size.
  - Progress state for every parallel segment (Start, End, and Current byte offsets).
  - Selected season/episode override configurations (`SeasonOverride` and `EpisodeOverride`).
- **Finalization**: Upon completion, the downloader reads the metadata, truncates the file to its original size (removing the 4KB footer), and moves it to the final destination.

### 2.2 Sparse File Support
To prevent disk fragmentation and ensure immediate file allocation, the app utilizes **Windows Sparse Files**.
- **`DeviceIoControl`**: Uses the `FSCTL_SET_SPARSE` control code via P/Invoke on Windows.
- **Benefit**: Allows the OS to allocate the full file size instantly while only occupying the physical space of the downloaded chunks on the drive.

---

## 3. Heuristic Metadata Resolution (`Features/Metadata_Resolver/MetadataResolver.cs`)

`MetadataResolver` serves as the **centralized engine** for all name-to-metadata parsing across the application. It replaces brittle, ad-hoc regex extraction with a sophisticated **Signal-Based Heuristic Engine** that identifies content without querying external databases.

- **Unified Source of Truth**: Whether identifying a torrent's category (Movie, Show, Game) or extracting specific TV show seasons/episodes for the TUI and path generation, all logic flows through this class.
- **Three Domains**: It parses every name three times, scoring it as a **Media** candidate, a **Game** candidate, and a **Software** candidate.
- **Signal Weights**:
  - `YEAR_DETECTED` (+0.18 for Media).
  - `GLOBAL_RESOLUTION` (+0.15 for Media, -0.15 for Games).
  - `TYPE_GAME_REGEX` (+0.30 for Games).
  - `TV_STANDARD` (SxxExx) (+0.45 for Media).
- **Confidence Scoring**: Each domain accumulates "Signal" weights. The domain with the highest confidence "wins," and its metadata is used for categorization, path generation, and real-time display.

---

## 4. Centralized Error Handling (`Models/Exceptions.cs`)

To maintain a clean TUI, errors are not simply thrown; they are **Polymorphic**.

- **`IPrintableException`**: An interface implemented by all custom exceptions.
- **The `Print()` Pattern**: Custom exceptions contain their own rendering logic using `Spectre.Console`. Exceptions include:
  - `RealDebridApiException` / `TorBoxApiException`: Renders formatted debrid-specific API response failures.
  - `ConfigurationException`: Outlines invalid configuration setups.
  - `MagnetException`: Handles malformed magnet link parsing.
  - `DownloadException`: Catches connection drops or segment failures.
- **Graceful Termination**: The `TerminationException` is a specialized `OperationCanceledException` that provides a red error message to the user before safely exiting the download loop, ensuring that partial files are handled according to user settings.

---

## 5. Directory Organization (`Features/Metadata_Resolver/PathGenerator.cs`)

Downloads are automatically sorted into a logical hierarchy:
- **Movies**: `MediaRoot/Movies/Title (Year)/Filename.mkv`
- **TV Shows**: `MediaRoot/TV Shows/Title (Year)/Season XX/Filename.mkv`
- **Games**: `GamesRoot/Game Setups/Title/Filename.exe`
- **Others**: `OthersRoot/Other/Filename.zip`

### 5.1 Dynamic Season Resolution
When a user selects a range of seasons (e.g., `1-3`) or downloads all seasons from a pack, the `PathGenerator` performs **Per-File Resolution**. Instead of relying on a single global season setting, it utilizes the `MetadataResolver` to parse the filename of each unrestricted link to determine its specific `Season XX` folder, ensuring a perfectly organized local library even in complex multi-season runs.

---

## 6. Debrid Provider Manager (`Features/Debrid_Manager/`)

The application supports multiple debrid providers by abstracting core debrid actions.

- **`IDebridClient` Interface**: Exposes provider-agnostic operations for checking caching status, adding magnets, selecting file payloads, un-restricting links, and fetching torrent metadata.
- **`RealDebridClient`**: Connects to the Real-Debrid API using `HttpClient`. Handles REST requests, handles pagination/polling, and throws `RealDebridApiException`.
- **`TorBoxClient`**: Connects to the TorBox API. Implements the same `IDebridClient` operations via TorBox endpoints and throws `TorBoxApiException`.
- **Configuration-Driven Instantiation**: The active debrid client is chosen based on the `debrid_service` setting (`real_debrid` or `torbox`), loaded at runtime.

---

## 7. Secure Storage & Secrets Manager (`Features/Secrets_Manager/`)

To prevent API tokens from being accidentally checked into source control or exposed in public configuration files, `MediaDebrid-cli` stores API keys inside the platform's native credentials vault.

- **`ISecureStorage` Interface**: Defines async operations for `SaveAsync`, `LoadAsync`, and `DeleteAsync`.
- **`SecretsManagerFactory`**: Detects the host OS at runtime and instantiates the correct platform implementation:
  - **Windows (`SecureStorageWindows`)**: Integrates with the Win32 Credential Manager (`advapi32.dll` via P/Invoke) to store generic credentials prefixed with `MediaDebrid:`.
  - **macOS (`SecureStorageMacOS`)**: Integrates with the macOS Keychain (`Security.framework`) using native CFString, CFData, and SecItem API P/Invokes.
  - **Linux (`SecureStorageLinux`)**: Uses DBus (`Tmds.DBus` library) to communicate with the FreeDesktop Secret Service (`org.freedesktop.secrets`). If no session bus is available, it falls back to a locally encrypted JSON file (`linux_secrets.json`) secured with AES-GCM (128-bit key derived using PBKDF2/SHA-512 from the machine ID and current user).
