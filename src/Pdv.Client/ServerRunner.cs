using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pdv.Application.Maintenance;
using Pdv.Application.Remote;

namespace Pdv.Client;

/// <summary>
/// Executa um método de serviço da aplicação no servidor da loja. A chamada é escrita como no código local —
/// <c>runner.RunAsync&lt;CashService, Result&lt;X&gt;&gt;(c =&gt; c.OpenAsync(terminalId, userId, valor))</c> — e vira
/// "CashService/OpenAsync" com os argumentos avaliados e enviados em JSON.
/// </summary>
public sealed class ServerRunner(PdvServerClient client)
{
    /// <summary>Backup e restauração podem levar minutos em bancos grandes.</summary>
    private static readonly TimeSpan LongTimeout = TimeSpan.FromMinutes(30);

    public async Task<TResult> RunAsync<TService, TResult>(Expression<Func<TService, Task<TResult>>> call) where TService : notnull =>
        (TResult)(await InvokeAsync(call, typeof(TResult)))!;

    public Task RunAsync<TService>(Expression<Func<TService, Task>> call) where TService : notnull =>
        InvokeAsync(call, null);

    private Task<object?> InvokeAsync(LambdaExpression call, Type? resultType)
    {
        var (service, method, args) = Describe(call);
        var timeout = service == typeof(IBackupManager) ? LongTimeout : PdvServerClient.DefaultTimeout;
        return client.InvokeAsync(ServiceName(service), method.Name, args, resultType, timeout);
    }

    public static string ServiceName(Type service) => service.Name;

    /// <summary>Extrai serviço, método e valores dos argumentos de <c>s =&gt; s.Metodo(a, b)</c>.</summary>
    public static (Type Service, MethodInfo Method, JsonArray Args) Describe(LambdaExpression call)
    {
        if (call.Body is not MethodCallExpression body || body.Object != call.Parameters[0])
            throw new ArgumentException("A chamada remota deve ser uma única chamada de método do serviço: s => s.Metodo(...).", nameof(call));

        var parameters = body.Method.GetParameters();
        var args = new JsonArray();
        for (var i = 0; i < parameters.Length; i++)
        {
            var type = parameters[i].ParameterType;
            if (type == typeof(CancellationToken)) { args.Add(null); continue; }
            var value = Evaluate(body.Arguments[i]);
            args.Add(JsonSerializer.SerializeToNode(value, type, RemoteApi.Json));
        }
        return (call.Parameters[0].Type, body.Method, args);
    }

    private static object? Evaluate(Expression expression) => expression switch
    {
        ConstantExpression c => c.Value,
        MemberExpression { Expression: ConstantExpression owner, Member: FieldInfo field } => field.GetValue(owner.Value),
        MemberExpression { Expression: ConstantExpression owner, Member: PropertyInfo property } => property.GetValue(owner.Value),
        _ => Expression.Lambda<Func<object?>>(Expression.Convert(expression, typeof(object))).Compile(preferInterpretation: true)(),
    };
}
