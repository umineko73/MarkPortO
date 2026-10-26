// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 umineko73
//
// This file is part of MarkPort.
//
// MarkPort is free software: you can redistribute it and/or modify it under the terms of
// the GNU General Public License, version 3 only, as published by the Free Software Foundation.
//
// MarkPort is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY;
// without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
// See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with MarkPort.
// If not, see <https://www.gnu.org/licenses/gpl-3.0.html>.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// A stand-in for the commands claude, codex and opencode (FR-056). It takes the same arguments, reads the prompt
// from the standard input and answers in the form of the command it is called as. What it does is chosen with the
// environment variable FAKE_VLM_MODE; FAKE_VLM_LOG names a file that gets one JSON line about each call.
//
// Modes: ok (default), nousage, notloggedin, usagelimit, ratelimit, imagetoolarge, badoutput, empty, hang, hangtree,
// writefile, fail.

Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = new UTF8Encoding(false);

var mode = Environment.GetEnvironmentVariable("FAKE_VLM_MODE") ?? "ok";

if (args.Contains("--version"))
{
    Console.WriteLine("fake-vlm 1.0.0");
    return 0;
}

var kind = args.Contains("-p") ? "claude" : args.Length > 0 && args[0] == "exec" ? "codex" : args.Length > 0 && args[0] == "run" ? "opencode" : "unknown";
var stdin = Console.In.ReadToEnd();
var prompt = kind == "claude" ? ClaudePrompt(stdin) : stdin;

Log(kind, mode, stdin);

if (mode == "hang")
{
    Thread.Sleep(Timeout.Infinite);
}

if (mode == "hangtree")
{
    // A child that outlives a polite request: the caller must end the whole tree.
    var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, [.. args]) { UseShellExecute = false, RedirectStandardInput = true, Environment = { ["FAKE_VLM_MODE"] = "hang" } });
    var pidFile = Environment.GetEnvironmentVariable("FAKE_VLM_PIDFILE");
    if (pidFile is not null)
    {
        File.WriteAllText(pidFile, child!.Id.ToString());
    }

    Thread.Sleep(Timeout.Infinite);
}

if (mode == "writefile")
{
    try
    {
        File.WriteAllText("hello.txt", "hi");
    }
    catch (Exception)
    {
        // a read-only place: nothing to do
    }
}

switch (mode)
{
    case "notloggedin":
        return Fail(kind, "Not logged in. Please run login first.");
    case "usagelimit":
        return Fail(kind, "You have reached your usage limit. Try again later.");
    case "ratelimit":
        return Fail(kind, "Rate limit exceeded (429 Too Many Requests).");
    case "imagetoolarge":
        return Fail(kind, "The image is too large: it exceeds the maximum size.");
    case "fail":
        return Fail(kind, "Something unexpected went wrong.");
    case "badoutput":
        Console.WriteLine("this is not what the command should print");
        return 0;
    case "empty":
        return Answer(kind, string.Empty, withUsage: true);
}

var reply = Environment.GetEnvironmentVariable("FAKE_VLM_REPLY") ?? "FAKE:" + prompt;
return Answer(kind, reply, withUsage: mode != "nousage");

static string ClaudePrompt(string stdin)
{
    try
    {
        var line = stdin.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
        var content = JsonNode.Parse(line)!["message"]!["content"]!.AsArray();
        return content.First(c => c!["type"]!.GetValue<string>() == "text")!["text"]!.GetValue<string>();
    }
    catch (Exception)
    {
        return stdin;
    }
}

static void Log(string kind, string mode, string stdin)
{
    if (Environment.GetEnvironmentVariable("FAKE_VLM_LOG") is not { Length: > 0 } path)
    {
        return;
    }

    var entry = new JsonObject
    {
        ["kind"] = kind,
        ["mode"] = mode,
        ["args"] = new JsonArray([.. Environment.GetCommandLineArgs().Skip(1).Select(a => (JsonNode?)a)]),
        ["cwd"] = Environment.CurrentDirectory,
        ["stdin"] = stdin,
        ["opencodeConfig"] = Environment.GetEnvironmentVariable("OPENCODE_CONFIG_CONTENT"),
    };
    File.AppendAllText(path, entry.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
}

static int Answer(string kind, string reply, bool withUsage)
{
    switch (kind)
    {
        case "claude":
            Console.WriteLine("""{"type":"system","subtype":"init"}""");
            var result = new JsonObject { ["type"] = "result", ["subtype"] = "success", ["is_error"] = false, ["result"] = reply };
            if (withUsage)
            {
                result["total_cost_usd"] = 0.0123;
                result["usage"] = new JsonObject { ["input_tokens"] = 2, ["cache_creation_input_tokens"] = 100, ["cache_read_input_tokens"] = 50, ["output_tokens"] = 7 };
            }

            Console.WriteLine(result.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return 0;

        case "codex":
            var output = ValueAfter("-o");
            if (output is not null)
            {
                File.WriteAllText(output, reply, new UTF8Encoding(false));
            }

            Console.WriteLine("""{"type":"thread.started","thread_id":"t"}""");
            Console.WriteLine(new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject { ["id"] = "item_0", ["type"] = "agent_message", ["text"] = reply } }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Console.WriteLine(withUsage
                ? """{"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":10,"output_tokens":7}}"""
                : """{"type":"turn.completed"}""");
            return 0;

        default: // opencode
            Console.WriteLine("""{"type":"step_start","part":{"type":"step-start"}}""");
            Console.WriteLine(new JsonObject { ["type"] = "text", ["part"] = new JsonObject { ["type"] = "text", ["text"] = reply } }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            Console.WriteLine(withUsage
                ? """{"type":"step_finish","part":{"type":"step-finish","tokens":{"total":120,"input":100,"output":20,"reasoning":0},"cost":0}}"""
                : """{"type":"step_finish","part":{"type":"step-finish"}}""");
            return 0;
    }
}

static int Fail(string kind, string message)
{
    switch (kind)
    {
        case "claude":
            Console.WriteLine(new JsonObject { ["type"] = "result", ["subtype"] = "error", ["is_error"] = true, ["result"] = message }.ToJsonString());
            return 1;
        case "codex":
            Console.WriteLine(new JsonObject { ["type"] = "error", ["message"] = message }.ToJsonString());
            Console.Error.WriteLine(message);
            return 1;
        default:
            Console.WriteLine(new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["name"] = "UnknownError", ["data"] = new JsonObject { ["message"] = message } } }.ToJsonString());
            return 1;
    }
}

static string? ValueAfter(string flag)
{
    var all = Environment.GetCommandLineArgs();
    var index = Array.IndexOf(all, flag);
    return index >= 0 && index + 1 < all.Length ? all[index + 1] : null;
}
