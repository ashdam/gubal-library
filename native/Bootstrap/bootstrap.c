#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <wchar.h>
#include <stdio.h>

typedef int (__cdecl *init_fn)(int, const wchar_t **, const void *, void **);
typedef int (__cdecl *delegate_fn)(void *, int, void **);
typedef int (__cdecl *close_fn)(void *);
typedef int (__stdcall *load_fn)(const wchar_t *, const wchar_t *, const wchar_t *, const wchar_t *, void *, void **);
typedef int (__stdcall *entry_fn)(void *, int);
typedef struct { wchar_t fxr[1024]; wchar_t settings[1024]; int validate; } arguments;

__declspec(dllexport) DWORD WINAPI Start(void *pointer)
{
    arguments *args = (arguments *)pointer;
    if (!args->validate && GetModuleHandleW(L"coreclr.dll") != NULL) return 10;
    HMODULE self = NULL;
    wchar_t directory[1024], assembly[1200];
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        (const wchar_t *)&Start, &self)) return 11;
    if (!GetModuleFileNameW(self, directory, 1024)) return 12;
    wchar_t *last = wcsrchr(directory, L'\\');
    if (!last) return 13;
    *last = 0;
    swprintf(assembly, 1200, L"%ls\\GubalStandalone.Runtime.dll", directory);
    HMODULE fxr = LoadLibraryW(args->fxr);
    if (!fxr) return 14;
    init_fn init = (init_fn)GetProcAddress(fxr, "hostfxr_initialize_for_dotnet_command_line");
    delegate_fn get_delegate = (delegate_fn)GetProcAddress(fxr, "hostfxr_get_runtime_delegate");
    close_fn close = (close_fn)GetProcAddress(fxr, "hostfxr_close");
    if (!init || !get_delegate || !close) return 15;
    void *context = NULL;
    const wchar_t *command[] = { assembly };
    int result = init(1, command, NULL, &context);
    if (result < 0 || !context) return 16;
    load_fn load = NULL;
    result = get_delegate(context, 5, (void **)&load);
    close(context);
    if (result < 0 || !load) return 17;
    entry_fn entry = NULL;
    result = load(assembly, L"GubalStandalone.Entry, GubalStandalone.Runtime",
        args->validate ? L"Validate" : L"Initialize", NULL, NULL, (void **)&entry);
    if (result < 0 || !entry) return 18;
    return entry(args->settings, (int)((wcslen(args->settings) + 1) * sizeof(wchar_t)));
}

#ifdef GUBAL_HOST_CHECK
int wmain(int argc, wchar_t **argv)
{
    if (argc != 3 || wcslen(argv[1]) >= 1024 || wcslen(argv[2]) >= 1024) return 2;
    arguments args = {0};
    wcscpy(args.fxr, argv[1]);
    wcscpy(args.settings, argv[2]);
    args.validate = 1;
    int result = (int)Start(&args);
    printf("Native host validation result: %d\n", result);
    return result;
}
#endif
