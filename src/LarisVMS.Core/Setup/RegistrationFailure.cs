using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using LarisVMS.Core.Logging;

namespace LarisVMS.Core.Setup;

/// <summary>
/// Why a node's or proxy's first registration didn't work, in words instead of a stack trace: a
/// mistyped server address, a server that isn't there, a refused key. In a container or service log
/// this is all the person setting it up gets to see.
/// </summary>
public static class RegistrationFailure
{
    /// <summary>What to say when no server address or registration key was given.</summary>
    public static string NotRegistered(string kind, bool isWindows, bool inContainer)
    {
        if (inContainer)
            return "Not yet registered. Set LARISVMS_SERVER_URL and LARISVMS_REGISTRATION_KEY in this container's environment.";
        if (isWindows)
            return "Not yet registered. Run with --server-url <url> --registration-key <key> " +
                   "(or set LARISVMS_SERVER_URL / LARISVMS_REGISTRATION_KEY) the first time.";
        return $"Not yet registered. Pair this {kind} with: sudo larisvms-{kind} setup --server-url <url> --registration-key <key>";
    }

    /// <summary>Null when <paramref name="serverUrl"/> is an http(s) address, else what's wrong with it.</summary>
    public static string? CheckServerUrl(string serverUrl) =>
        Uri.TryCreate(serverUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? null
            : $"The server address (LARISVMS_SERVER_URL or --server-url) isn't valid: \"{serverUrl}\". " +
              "It must look like https://vms.example.com:8444.";

    /// <summary>The reason a registration request failed, or null for a failure this doesn't know (a
    /// bug, which keeps its stack trace).</summary>
    public static string? Describe(Exception ex, string serverUrl) => ex switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
            $"{serverUrl} refused the registration key. Use the key shown under Nodes → Updates and registration, or a one-time code that hasn't been used or expired.",
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
            $"{serverUrl} is refusing registrations for now because there were too many attempts. It's tried again at the next start.",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
            $"{serverUrl} answered, but it isn't a LarisVMS server. Check the address and port.",
        HttpRequestException { StatusCode: { } status } =>
            $"{serverUrl} refused the registration (HTTP {(int)status}). Its log has the reason.",
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } http =>
            $"Can't find the server {serverUrl}: {http.Message}. Check the name, and that this machine's DNS knows it.",
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } http =>
            $"Can't make a secure connection to {serverUrl}: {Reason(http)}. Check that the port is the server's HTTPS port, " +
            "and that its certificate is the one given in LARISVMS_SERVER_CERT_THUMBPRINT, if any.",
        HttpRequestException http =>
            $"Can't connect to {serverUrl}: {Reason(http)}. Check that the LarisVMS web server is running, and the address and port.",
        TaskCanceledException =>
            $"{serverUrl} didn't answer in time. Check the address and port, and that no firewall is blocking it.",
        // A reverse proxy without a site for this name, or a different program, answering 200.
        JsonException or NotSupportedException =>
            $"{serverUrl} answered, but not like a LarisVMS server. Check the address and port, any reverse proxy in between, and that its setup wizard is finished.",
        _ => null,
    };

    /// <summary>Prints <paramref name="message"/>, writes it to the log file the Logs page reads (a
    /// Windows service has no console), and ends the process.</summary>
    public static void Exit(string message, string logPrefix)
    {
        Console.Error.WriteLine(message);
        try
        {
            using var file = new FileLoggerProvider(LarisPaths.LogRoot, logPrefix, LogLevel.Information);
            file.CreateLogger("Registration").LogError("{Message}", message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It was printed.
        }
        Environment.Exit(1);
    }

    // "The SSL connection could not be established, see inner exception." says nothing by itself.
    private static string Reason(HttpRequestException ex) =>
        (ex.InnerException is System.Security.Authentication.AuthenticationException inner ? inner.Message : ex.Message).TrimEnd('.');
}
