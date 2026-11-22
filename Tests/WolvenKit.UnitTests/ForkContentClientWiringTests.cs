using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using WolvenKit.App.Helpers;
using WolvenKit.App.ViewModels.Shell;
using Xunit;

namespace WolvenKit.UnitTests;

/// <summary>
/// Compiled-call-site guards, not startup/network execution tests. Transport behavior
/// and real script/template commits are verified separately against the same build.
/// </summary>
public class ForkContentClientWiringTests
{
    [Fact]
    public void ScriptConsumerUsesOptInContentClientFactory()
    {
        var entry = typeof(AppViewModel).GetMethod("CheckForScriptUpdates", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var state = entry.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
        var moveNext = state.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        AssertConsumerCallsFactory(moveNext, "Script");
    }

    [Fact]
    public void TemplateSharedClientUsesOptInContentClientFactory()
    {
        var initializer = typeof(RedTypeTemplateServiceHelper).TypeInitializer!;
        AssertConsumerCallsFactory(initializer, "Template");
    }

    private static void AssertConsumerCallsFactory(MethodBase method, string consumer)
    {
        var calls = ReadCalls(method).ToArray();
        var factoryCalls = calls.Count(call => call.DeclaringType == typeof(ProtonHttpClientFactory)
            && call.Name == nameof(ProtonHttpClientFactory.CreateHttpClient)
            && call.GetParameters().Length == 0);
        Assert.True(factoryCalls == 1,
            $"{consumer} consumer must create its content client through the exact-opt-in factory; actual factory calls={factoryCalls}");
        Assert.DoesNotContain(calls, call => call is ConstructorInfo && call.DeclaringType == typeof(HttpClient));
    }

    private static readonly IReadOnlyDictionary<short, OpCode> Codes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .GroupBy(code => code.Value)
        .ToDictionary(group => group.Key, group => group.First());

    // Decode actual instruction boundaries. Searching for raw call-token bytes can
    // mistake an unrelated operand for a call and produce a false wiring verdict.
    private static IEnumerable<MethodBase> ReadCalls(MethodBase method)
    {
        var body = method.GetMethodBody()?.GetILAsByteArray()
            ?? throw new InvalidOperationException("The compiled consumer has no IL body.");
        var position = 0;
        while (position < body.Length)
        {
            short value = body[position++];
            if (value == 0xfe)
            {
                if (position >= body.Length) throw new InvalidOperationException("Truncated two-byte opcode.");
                value = unchecked((short)(0xfe00 | body[position++]));
            }
            if (!Codes.TryGetValue(value, out var code)) throw new InvalidOperationException("Unknown opcode.");
            var operand = position;
            var size = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod
                    or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok
                    or OperandType.InlineType or OperandType.ShortInlineR => 4,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => SwitchSize(body, operand),
                _ => throw new InvalidOperationException("Unsupported IL operand type.")
            };
            if (size < 0 || position > body.Length - size) throw new InvalidOperationException("Truncated IL operand.");
            position += size;
            if (code.OperandType == OperandType.InlineMethod)
            {
                var resolved = method.Module.ResolveMethod(BitConverter.ToInt32(body, operand),
                    method.DeclaringType?.GetGenericArguments(),
                    method is MethodInfo info && info.IsGenericMethod ? info.GetGenericArguments() : null);
                if (resolved is not null) yield return resolved;
            }
        }
    }

    private static int SwitchSize(byte[] body, int operand)
    {
        if (operand > body.Length - 4) throw new InvalidOperationException("Truncated switch operand.");
        var count = BitConverter.ToInt32(body, operand);
        if (count < 0 || count > (body.Length - operand - 4) / 4)
            throw new InvalidOperationException("Invalid switch operand length.");
        return 4 + count * 4;
    }
}
