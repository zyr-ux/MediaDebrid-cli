# MediaDebrid-cli Architecture & Internal Documentation

This document provides a deep-dive into the custom engineering and architectural decisions that make `MediaDebrid-cli` a robust and high-performance media downloader.

## 1. Custom TUI Framework (`TuiApp.cs`)

The application utilizes `Spectre.Console` for its rich Terminal User Interface (TUI), but extends it significantly with custom components to handle high-volume data and complex input scenarios.

### 1.1 Typewriter Input Reader (`ReadLineWithEffectAsync`)
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

---

## 2. Resumable Download Engine (`Services/Downloader.cs`)

The core of the application is a high-performance, segmented downloader designed to survive application restarts and network failures.

### 2.1 The 4KB Metadata Footer
Unlike traditional downloaders that use sidecar files (e.g., `.part` files), `MediaDebrid-cli` uses a **Binary Footer** system.

- **Storage**: A fixed 4096-byte (4KB) area is reserved at the absolute end of every `.mdebrid` temporary file.
- **Magic Marker**: The last 8 bytes are the ASCII string `MDEBRID!`. This allows the app to instantly verify if a file was created by this application.
- **JSON Metadata**: The area preceding the marker stores a `ResumeMetadata` object containing:
  - Original Magnet URI.
  - File ID on Real-Debrid.
  - Total file size.
  - Progress state for every parallel segment (Start, End, and Current byte offsets).
- **Finalization**: Upon completion, the downloader reads the metadata, truncates the file to its original size (removing the 4KB footer), and moves it to the final destination.

### 2.2 Sparse File Support
To prevent disk fragmentation and ensure immediate file allocation, the app utilizes **Windows Sparse Files**.
- **`DeviceIoControl`**: Uses the `FSCTL_SET_SPARSE` control code via P/Invoke on Windows.
- **Benefit**: Allows the OS to allocate the full file size instantly while only occupying the physical space of the downloaded chunks on the drive.

---

## 3. Heuristic Metadata Resolution (`Services/MetadataResolver.cs`)

The app uses a sophisticated **Signal-Based Heuristic Engine** to identify what is inside a torrent name without querying external databases.

- **Three Domains**: It parses every name three times, scoring it as a **Media** candidate, a **Game** candidate, and a **Software** candidate.
- **Signal Weights**:
  - `YEAR_DETECTED` (+0.18 for Media).
  - `GLOBAL_RESOLUTION` (+0.15 for Media, -0.15 for Games).
  - `TYPE_GAME_REGEX` (+0.30 for Games).
  - `TV_STANDARD` (SxxExx) (+0.45 for Media).
- **Confidence Scoring**: Each domain accumulates "Signal" weights. The domain with the highest confidence "wins," and its metadata is used for path generation.

---

## 4. Centralized Error Handling (`Models/Exceptions.cs`)

To maintain a clean TUI, errors are not simply thrown; they are **Polymorphic**.

- **`IPrintableException`**: An interface implemented by all custom exceptions.
- **The `Print()` Pattern**: Exceptions like `RealDebridApiException` or `MagnetException` contain their own rendering logic using `Spectre.Console`. 
- **Graceful Termination**: The `TerminationException` is a specialized `OperationCanceledException` that provides a red error message to the user before safely exiting the download loop, ensuring that partial files are handled according to user settings.

---

## 5. Directory Organization (`Services/PathGenerator.cs`)

Downloads are automatically sorted into a logical hierarchy:
- **Movies**: `MediaRoot/Movies/Title (Year)/Filename.mkv`
- **TV Shows**: `MediaRoot/TV Shows/Title (Year)/Season XX/Filename.mkv`
- **Games**: `GamesRoot/Game Setups/Title (Year)/Filename.exe`
- **Others**: `OthersRoot/Other/Filename.zip`

### 5.1 Dynamic Season Resolution
When a user selects a range of seasons (e.g., `1-3`) or downloads all seasons from a pack, the `PathGenerator` performs **Per-File Resolution**. Instead of relying on a single global season setting, it parses the filename of each unrestricted link to determine its specific `Season XX` folder, ensuring a perfectly organized local library even in complex multi-season runs.
