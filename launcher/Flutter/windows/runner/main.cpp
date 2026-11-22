#include <flutter/dart_project.h>
#include <flutter/flutter_view_controller.h>
#include <windows.h>
#include <algorithm>
#include <filesystem>
#include <fstream>
#include <memory>
#include <shlobj.h>

#include "flutter_window.h"
#include "utils.h"

int APIENTRY wWinMain(_In_ HINSTANCE instance, _In_opt_ HINSTANCE prev,
                      _In_ wchar_t *command_line, _In_ int show_command) {
  // The native UI must be single-instance too: a rejected second core used to
  // leave another Flutter process holding the same update directory open.
  const auto arguments = GetCommandLineArguments();
  const bool guard_check = arguments.size() == 1 && arguments[0] == "--verify-launch-guard";
  // Headless update health check: BundleInstaller starts the new app with
  // --update-health <ready-file> and waits for that file to appear. This must
  // NOT go through the UI single-instance gate: during an update the old UI is
  // still alive, so the gate rejected the check (exit 2), no ready file was ever
  // written, and the installer rolled back after 60s. That made the first launch
  // after an update fail intermittently. Verify the new binary can load and the
  // core can start, then write the receipt directly.
  std::wstring health_ready;
  bool health_check = false;
  for (const auto& arg : arguments) {
    if (arg.rfind("--update-health", 0) == 0) health_check = true;
    else if (!arg.empty() && arg[0] != '-' && health_ready.empty()) health_ready = std::wstring(arg.begin(), arg.end());
  }
  if (health_check) {
    // Validate the new install is loadable by running the headless core guard
    // check, which exits 0 only when the core and anti-cheat runtime are intact.
    const auto exe_dir = std::filesystem::path(__argv[0]).parent_path();
    const auto core = exe_dir / "DNF90.Core.exe";
    if (std::filesystem::exists(core)) {
      STARTUPINFOW si{}; si.cb = sizeof(si);
      PROCESS_INFORMATION pi{};
      std::wstring cmd = L"\"" + core.wstring() + L"\" --verify-core-health";
      if (CreateProcessW(core.c_str(), cmd.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi)) {
        WaitForSingleObject(pi.hProcess, 15000);
        DWORD code = 1; GetExitCodeProcess(pi.hProcess, &code);
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
        if (code != 0) return 3;
      } else return 3;
    }
    if (!health_ready.empty()) {
      std::ofstream(health_ready) << "ok";
    }
    return 0;
  }
  HANDLE raw_gate = CreateMutexW(nullptr, TRUE, L"Local\\DNF90Launcher.UI.v1");
  const bool duplicate = raw_gate && GetLastError() == ERROR_ALREADY_EXISTS;
  const auto closer = [](void* value) { if (value) CloseHandle(value); };
  std::unique_ptr<void, decltype(closer)> gate(raw_gate, closer);
  bool core_running = false;
  // Match Host/Program.cs LaunchData.Resolve exactly: green portable layout is
  // <entry>\DNF90Launcher-Data\state-anticheat, not %LOCALAPPDATA%\DNF90Launcher.
  // Checking the wrong path let a stale core hold instance.lock while this
  // precheck passed, so the new core died on startup ("core exited" message).
  std::wstring core_lock;
  const DWORD env_len = GetEnvironmentVariableW(L"DNF90_LAUNCH_DIRECTORY", nullptr, 0);
  if (env_len > 0) {
    std::wstring entry(env_len - 1, L'\0');
    GetEnvironmentVariableW(L"DNF90_LAUNCH_DIRECTORY", entry.data(), env_len);
    core_lock = entry + L"\\DNF90Launcher-Data\\state-anticheat\\instance.lock";
  } else {
    PWSTR local = nullptr;
    if (SUCCEEDED(SHGetKnownFolderPath(FOLDERID_LocalAppData, 0, nullptr, &local))) {
      core_lock = std::wstring(local) + L"\\DNF90Launcher-anticheat\\instance.lock";
      CoTaskMemFree(local);
    }
  }
  if (!core_lock.empty()) {
    HANDLE file = CreateFileW(core_lock.c_str(), GENERIC_READ | GENERIC_WRITE, 0,
                             nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) core_running = GetLastError() == ERROR_SHARING_VIOLATION;
    else CloseHandle(file);
  }
  if (!gate || duplicate || core_running) {
    if (!guard_check) {
      const auto existing = FindWindowW(nullptr, L"PurpleMagic");
      if (existing) { ShowWindow(existing, SW_RESTORE); SetForegroundWindow(existing); }
    }
    return 2;
  }
  if (guard_check) return 0; // Headless verification, before COM/Flutter/window creation.
  // Attach to console when present (e.g., 'flutter run') or create a
  // new console when running with a debugger.
  if (!::AttachConsole(ATTACH_PARENT_PROCESS) && ::IsDebuggerPresent()) {
    CreateAndAttachConsole();
  }

  // Initialize COM, so that it is available for use in the library and/or
  // plugins.
  ::CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
  SetCurrentProcessExplicitAppUserModelID(L"PurpleMagic.Launcher");

  flutter::DartProject project(L"data");

  std::vector<std::string> command_line_arguments =
      GetCommandLineArguments();

  project.set_dart_entrypoint_arguments(std::move(command_line_arguments));

  FlutterWindow window(project);
  RECT work = {};
  SystemParametersInfo(SPI_GETWORKAREA, 0, &work, 0);
  const double scale = GetDpiForSystem() / 96.0;
  const auto available_width = static_cast<unsigned int>((work.right - work.left) / scale);
  const auto available_height = static_cast<unsigned int>((work.bottom - work.top) / scale);
  const auto width = std::min(1440u, available_width - 48u);
  const auto height = std::min(810u, available_height - 48u);
  Win32Window::Point origin((available_width - width) / 2u, (available_height - height) / 2u);
  Win32Window::Size size(width, height);
  if (!window.Create(L"PurpleMagic", origin, size)) {
    return EXIT_FAILURE;
  }
  window.SetQuitOnClose(true);

  ::MSG msg;
  while (::GetMessage(&msg, nullptr, 0, 0)) {
    ::TranslateMessage(&msg);
    ::DispatchMessage(&msg);
  }

  ::CoUninitialize();
  return EXIT_SUCCESS;
}
