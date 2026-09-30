namespace ZapretSmart.Core.Tests;

/// <summary>
/// Правило брандмауэра раздачи одно на машину, а проекты тестов dotnet test гоняет параллельно. Живые тесты
/// брандмауэра из разных проектов идут по очереди, иначе один снимет правило другого посреди проверки.
/// Замок именованный: он общий для всех процессов в сеансе.
/// </summary>
internal static class FirewallTestLock
{
    public static IDisposable Take()
    {
        var semaphore = new Semaphore(1, 1, @"Local\ZapretSmartFirewallTests");
        if (!semaphore.WaitOne(TimeSpan.FromMinutes(3)))
        {
            semaphore.Dispose();
            throw new TimeoutException("живой тест брандмауэра в другом проекте не освободил замок за 3 минуты");
        }
        return new Releaser(semaphore);
    }

    private sealed class Releaser(Semaphore semaphore) : IDisposable
    {
        public void Dispose()
        {
            semaphore.Release();
            semaphore.Dispose();
        }
    }
}
