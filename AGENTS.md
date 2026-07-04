# MediaDebrid-cli Agent Guidelines

Welcome to the `MediaDebrid-cli` repository! This document provides AI agents (and human contributors) with essential context, architectural understanding, and coding standards for this project.

## Project Overview
`MediaDebrid-cli` is a command-line interface (CLI) application built using **.NET 10.0** and C#. It acts as a powerful, feature-rich downloader utilizing debrid services (Real-Debrid / TorBox) to resolve magnet links and download media files efficiently. It features a rich Terminal User Interface (TUI) for an enhanced user experience, including resumable parallel downloads.

### Core Technologies
- **.NET 10.0** (C#)
- **Spectre.Console**: Used extensively for the Terminal User Interface (TUI), including progress bars, spinners, layout, and styled text.
- **System.CommandLine**: Handles CLI argument parsing and command routing.
- **DotNetEnv**: Loads environment variables from `.env` files.

---

## Architecture & Directory Structure

The codebase is organized cleanly using a feature-based structure to separate concerns.

### `Program.cs` & `Settings.cs`
- **`Program.cs`**: The entry point of the application. It configures `System.CommandLine`, parses arguments, routes commands (interactive, `unres`, `resume`, `set`, `setup`), loads environment variables, and boots up the `TuiApp`.
- **`Settings.cs`**: Manages application-level settings, integrating both CLI arguments, `.env` configurations, and native secure vault credentials.

### `Tui/` (Terminal UI)
- **`Tui/TuiApp.cs`**: The core of the user interface. It manages the Spectre.Console components (e.g., `Progress`, layouts, panels, tables). It is responsible for orchestrating the overall flow, displaying download progress, handling user input for cancellation/pausing, and logging formatted status messages.
- **`Tui/PrintGap.cs`**: **CRITICAL** - Central stateful spacing manager that coordinates all console output margins. It tracks if the console has already printed a newline, preventing double gaps and managing progress bar padding.
- **`Tui/Components.cs`**: UI templates and prompts (onboarding setup, configuration listing, logo showing, text inputs).

### `Features/` (Core Feature Components)
Contains modular subdirectories containing business logic:
- **`Features/Debrid_Manager/`**:
  - `IDebridClient.cs`: Unified abstraction interface for all debrid services.
  - `RealDebridClient.cs` / `TorBoxClient.cs`: Endpoints wrapper for Real-Debrid and TorBox respectively.
- **`Features/Download_Manager/`**:
  - `Downloader.cs`: Manages parallel chunk downloading and state finalization.
  - `MagnetParser.cs`: Parses and validates magnet structures.
  - `MediaWorkflowService.cs`: Orchestrates flow transitions between adding magnet, waiting for debrid caching/completion, and starting downloading.
- **`Features/Metadata_Resolver/`**:
  - `MetadataResolver.cs`: The **unified engine** for all media metadata identification. It uses signal-based heuristics to categorize content and extract season/episode data. All name-to-metadata parsing MUST be routed through this class to ensure consistency.
  - `PathGenerator.cs`: Constructs clean, organized, and valid file system paths for the downloaded media based on its metadata.
- **`Features/Secrets_Manager/`**:
  - `ISecureStorage.cs`: Abstract secure storage wrapper.
  - `SecretsManagerFactory.cs`: Factory returning platform-specific secure storage.
  - `SecureStorageWindows.cs` / `SecureStorageMacOS.cs` / `SecureStorageLinux.cs`: System-specific secure credential vault drivers.

### `Utilities/`
- **`Utilities/Utils.cs`**: General utility methods (formatting byte sizes, range parsing, configuration updating).

### `Models/` (Data Structures & Errors)
Contains POCO classes, DTOs, and application state objects.
- **`Exceptions.cs`**: **CRITICAL** Centralizes custom exceptions (e.g., `RealDebridApiException`, `TorBoxApiException`, `TerminationException`, `DownloadException`). Always use these predefined exceptions rather than throwing generic `Exception`s.
- **`RealDebridModels.cs` & `TorBoxModels.cs`**: JSON mapping models for Real-Debrid and TorBox API responses.
- **`DownloadProgressModel.cs` & `ResumeMetadata.cs`**: Models tracking active/resumable download states.
- **`AppSettings.cs`**: Structure mapping all configurations.

### `Serialization/`
- **`AppSettingsJsonContext.cs` & `MediaDebridJsonContext.cs`**: Native AOT-compatible JSON serialization context definitions.

---

## Coding Guidelines & Rules

When modifying or generating code for `MediaDebrid-cli`, adhere strictly to the following guidelines:

### 1. Terminal UI (TUI) Rules
- **Do not use standard `Console.WriteLine` or standard `AnsiConsole.WriteLine/MarkupLine`** directly for spacing or standard prints in the primary TUI flow. Instead, always route outputs through **`PrintGap`** (e.g., `PrintGap.Print()`, `PrintGap.MarkupLine()`, `PrintGap.Write()`) to preserve state-based margin tracking.
- **Rules of Spacing (`PrintGap`):**
  - Use `PrintGap.Print()` to insert visual separators; it self-corrects and prints exactly one empty line.
  - Use `PrintGap.Suppress()` before and after padded live blocks (like progress bars) to eliminate negative spacing margins.
  - Standard prints automatically reset the gap state so the next `Print()` is allowed.
- When updating progress or showing status, ensure thread-safety if interacting with Spectre.Console's `ProgressContext` from background tasks.
- Keep the TUI responsive. Heavy blocking operations should be offloaded to asynchronous tasks.

### 2. Error Handling & Exceptions
- **Always use `Models/Exceptions.cs`**: If you need to throw an error related to application logic, find the appropriate custom exception in `Exceptions.cs` or add a new one there. Do not create orphaned exception classes throughout the project.
- Ensure graceful degradation. Provide clear, human-readable error messages using `Spectre.Console`'s red markup (e.g., `[red]Error: ...[/]`) when things fail.

### 3. Asynchronous Programming
- The application relies heavily on `async/await`. Avoid using `.Result` or `.Wait()` which can cause deadlocks.
- Always pass and respect `CancellationToken`s. Downloads and API calls must be cancellable to support the TUI's "exit/cancel" features properly.

### 4. Language Features
- **Nullable Reference Types**: Enabled globally (`<Nullable>enable</Nullable>`). Ensure proper null-checking (`?`, `!`, `??`) to avoid compiler warnings.
- **C# 12/13 Features**: Utilize modern C# features like primary constructors, collection expressions (`[]`), and raw string literals where they improve readability.

### 5. Resumable Downloads
- The `Features/Download_Manager/Downloader.cs` uses a 4KB file footer to store JSON metadata (`ResumeMetadata`) allowing downloads to persist across application restarts. When modifying download logic, ensure this metadata is correctly parsed, updated, and re-written, avoiding data corruption.
- **Metadata Overrides**: Season and episode overrides are stored as `string?` to support complex ranges (e.g., `4-8`, `1,3,5`). The application utilizes `Utils.ParseRange` for validation and filtering. These selections are persisted in the `ResumeMetadata` footer.

---

## Development Workflow

- **Build**: `dotnet build`
- **Run**: `dotnet run -- [arguments]`
- **Format**: Follow standard C# naming conventions (PascalCase for classes/methods/properties, camelCase for variables/fields, `_camelCase` for private fields).

*When in doubt, prioritize user experience in the TUI and robust error handling!*
