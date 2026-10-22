/* SPDX-License-Identifier: MIT */
/* Keep .NET's managed payload in Resources without a shell or extra process. */
#include <errno.h>
#include <mach-o/dyld.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

int main(int argc, char *argv[])
{
    if (argc < 1 || argv == NULL || argv[0] == NULL) {
        fputs("The application launcher received no argument vector.\n", stderr);
        return 127;
    }
    uint32_t length = 0;
    (void)_NSGetExecutablePath(NULL, &length);
    char *executable = length > 0 ? malloc(length) : NULL;
    if (executable == NULL || _NSGetExecutablePath(executable, &length) != 0) {
        free(executable);
        fputs("Unable to locate the application launcher.\n", stderr);
        return 127;
    }
    char *resolved = realpath(executable, NULL);
    int resolve_error = errno;
    free(executable);
    if (resolved == NULL) {
        fprintf(stderr, "Unable to resolve the application launcher: %s\n", strerror(resolve_error));
        return 127;
    }
    char *separator = strrchr(resolved, '/');
    if (separator == NULL) {
        free(resolved);
        fputs("The application launcher has no parent directory.\n", stderr);
        return 127;
    }
    *separator = '\0';
    const char suffix[] = "/../Resources/publish/drcom4scutGUI";
    size_t payload_size = strlen(resolved) + sizeof(suffix);
    char *payload = malloc(payload_size);
    if (payload == NULL) {
        free(resolved);
        fputs("Unable to allocate the application bundle path.\n", stderr);
        return 127;
    }
    int written = snprintf(payload, payload_size, "%s%s", resolved, suffix);
    free(resolved);
    if (written < 0 || (size_t)written >= payload_size) {
        free(payload);
        fputs("The application bundle path is too long.\n", stderr);
        return 127;
    }
    argv[0] = payload;
    execv(payload, argv);
    int launch_error = errno;
    free(payload);
    fprintf(stderr, "Unable to start the application payload: %s\n", strerror(launch_error));
    return 127;
}
