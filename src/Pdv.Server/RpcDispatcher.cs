using System.Reflection;
using System.Text.Json;
using Pdv.Application.Remote;
using Pdv.Application.Terminals;

namespace Pdv.Server;

/// <summary>Um caixa tentou agir em nome de outro (parâmetro terminalId diferente da sua chave).</summary>
public sealed class RpcForbiddenException(string message) : Exception(message);

/// <summary>Serviço/método inexistente ou argumentos que não correspondem à assinatura.</summary>
public sealed class RpcBadRequestException(string message) : Exception(message);

/// <summary>
/// Liga "Serviço/Método" ao método público do serviço da aplicação. Só os serviços de <see cref="RemoteApi.Services"/>
/// são expostos, e só métodos assíncronos (Task/Task&lt;T&gt;) declarados neles.
/// </summary>
public sealed class RpcDispatcher
{
    private readonly Dictionary<string, RpcMethod> _methods = new(StringComparer.OrdinalIgnoreCase);

    public RpcDispatcher()
    {
        foreach (var service in RemoteApi.Services)
        {
            var methods = service.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => typeof(Task).IsAssignableFrom(m.ReturnType) && !m.IsSpecialName);
            foreach (var method in methods)
            {
                var key = $"{service.Name}/{method.Name}";
                if (!_methods.TryAdd(key, new RpcMethod(service, method)))
                    throw new InvalidOperationException($"Métodos sobrecarregados não podem ser chamados remotamente: {key}.");
            }
        }
    }

    public IEnumerable<RpcMethod> Methods => _methods.Values;

    public RpcMethod Find(string service, string method) =>
        _methods.GetValueOrDefault($"{service}/{method}") ?? throw new RpcBadRequestException($"Operação desconhecida: {service}/{method}.");

    public async Task<(object? Value, Type? Type)> InvokeAsync(IServiceProvider services, RpcMethod target, JsonElement args,
        TerminalInfo caller, CancellationToken ct)
    {
        var parameters = target.Parameters;
        if (args.ValueKind != JsonValueKind.Array || args.GetArrayLength() != parameters.Length)
            throw new RpcBadRequestException($"Argumentos inválidos para {target.Key}.");

        var values = new object?[parameters.Length];
        var i = 0;
        foreach (var arg in args.EnumerateArray())
        {
            var p = parameters[i];
            values[i] = p.ParameterType == typeof(CancellationToken)
                ? ct
                : Deserialize(arg, p.ParameterType, target.Key);
            CheckTerminal(p, values[i], caller);
            i++;
        }

        var instance = services.GetRequiredService(target.Service);
        var task = (Task)target.Method.Invoke(instance, BindingFlags.DoNotWrapExceptions, null, values, null)!;
        await task;
        return target.ResultType is null ? (null, null) : (target.ResultProperty!.GetValue(task), target.ResultType);
    }

    private static object? Deserialize(JsonElement arg, Type type, string key)
    {
        try
        {
            return arg.Deserialize(type, RemoteApi.Json);
        }
        catch (JsonException ex)
        {
            throw new RpcBadRequestException($"Argumento inválido para {key}: {ex.Message}");
        }
    }

    /// <summary>Um caixa só opera a própria gaveta e só grava vendas/devoluções em seu nome.</summary>
    private static void CheckTerminal(ParameterInfo parameter, object? value, TerminalInfo caller)
    {
        if (parameter.Name == RemoteApi.TerminalParameter && value is Guid id && id != caller.Id)
            throw new RpcForbiddenException("Este caixa não pode operar em nome de outro caixa.");
        if (value is not null && value.GetType().GetProperty("TerminalId") is { } prop && prop.PropertyType == typeof(Guid)
            && (Guid)prop.GetValue(value)! != caller.Id)
            throw new RpcForbiddenException("Este caixa não pode operar em nome de outro caixa.");
    }
}

public sealed class RpcMethod
{
    public RpcMethod(Type service, MethodInfo method)
    {
        Service = service;
        Method = method;
        Parameters = method.GetParameters();
        if (method.ReturnType.IsGenericType)
        {
            ResultType = method.ReturnType.GetGenericArguments()[0];
            ResultProperty = method.ReturnType.GetProperty(nameof(Task<int>.Result));
        }
    }

    public Type Service { get; }
    public MethodInfo Method { get; }
    public ParameterInfo[] Parameters { get; }
    public Type? ResultType { get; }
    public PropertyInfo? ResultProperty { get; }
    public string Key => $"{Service.Name}/{Method.Name}";
}
