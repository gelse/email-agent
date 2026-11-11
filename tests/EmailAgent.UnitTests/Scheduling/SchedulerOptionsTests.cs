using EmailAgent.Infrastructure;
using EmailAgent.Infrastructure.Scheduling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace EmailAgent.UnitTests.Scheduling;

/// <summary>
/// Unit tests for the scheduling options contract (M5T2 task file, "Tests"):
/// <c>Options_Defaults_TickMinJitterChannel</c>, <c>Options_NonPositiveTick_Rejected</c>,
/// <c>Options_JitterOutOfRange_Rejected</c>, plus configuration binding and DI registration facts.
/// </summary>
public class SchedulerOptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs)
    {
        var data = new Dictionary<string, string?>();
        foreach (var (key, value) in pairs)
        {
            data[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    /// <summary>
    /// Registers <see cref="ServiceCollectionExtensions.AddScheduling"/> on a bare
    /// container and resolves the options: <c>.Value</c> runs the same
    /// <c>Validate</c> predicate the host's ValidateOnStart uses.
    /// </summary>
    private static IOptions<T> Resolve<T>(IConfiguration config) where T : class
    {
        var services = new ServiceCollection();
        services.AddScheduling(config);
        return services.BuildServiceProvider().GetRequiredService<IOptions<T>>();
    }

    [Fact]
    public void Options_Defaults_TickMinJitterChannel()
    {
        var scheduler = Resolve<SchedulerOptions>(Config()).Value;
        Assert.Equal(TimeSpan.FromMinutes(1), scheduler.TickInterval);
        Assert.Equal(TimeSpan.FromMinutes(1), scheduler.MinInterval);
        Assert.Equal(10, scheduler.JitterPercent);

        var queue = Resolve<JobQueueOptions>(Config()).Value;
        Assert.Equal(256, queue.ChannelCapacity);
    }

    [Fact]
    public void Options_BindsFromConfiguration()
    {
        var config = Config(
            ("Scheduler:TickInterval", "00:00:30"),
            ("Scheduler:MinInterval", "00:05:00"),
            ("Scheduler:JitterPercent", "25"),
            ("Workers:ChannelCapacity", "64"));

        var scheduler = Resolve<SchedulerOptions>(config).Value;
        Assert.Equal(TimeSpan.FromSeconds(30), scheduler.TickInterval);
        Assert.Equal(TimeSpan.FromMinutes(5), scheduler.MinInterval);
        Assert.Equal(25, scheduler.JitterPercent);

        Assert.Equal(64, Resolve<JobQueueOptions>(config).Value.ChannelCapacity);
    }

    [Fact]
    public void Options_NonPositiveTick_Rejected()
    {
        var options = Resolve<SchedulerOptions>(Config(("Scheduler:TickInterval", "00:00:00")));

        var ex = Assert.Throws<OptionsValidationException>(() => _ = options.Value);
        Assert.Contains(SchedulerOptions.InvalidMessage, ex.Message);
    }

    [Fact]
    public void Options_JitterOutOfRange_Rejected()
    {
        var tooHigh = Resolve<SchedulerOptions>(Config(("Scheduler:JitterPercent", "101")));
        var negative = Resolve<SchedulerOptions>(Config(("Scheduler:JitterPercent", "-1")));

        Assert.Throws<OptionsValidationException>(() => _ = tooHigh.Value);
        Assert.Throws<OptionsValidationException>(() => _ = negative.Value);
    }

    [Fact]
    public void Options_NonPositiveFloor_Rejected()
    {
        var options = Resolve<SchedulerOptions>(Config(("Scheduler:MinInterval", "-00:00:01")));

        Assert.Throws<OptionsValidationException>(() => _ = options.Value);
    }

    [Fact]
    public void Options_NonPositiveChannelCapacity_Rejected()
    {
        var options = Resolve<JobQueueOptions>(Config(("Workers:ChannelCapacity", "0")));

        Assert.Throws<OptionsValidationException>(() => _ = options.Value);
    }

    [Fact]
    public void AddScheduling_RegistersQueueJitterAndHostedService()
    {
        var services = new ServiceCollection();
        services.AddScheduling(Config());

        Assert.Contains(services, s => s.ServiceType == typeof(IJobQueue));
        Assert.Contains(services, s => s.ServiceType == typeof(IJitterSource));
        Assert.Contains(services, s => s.ServiceType == typeof(TimeProvider));
        Assert.Contains(services, s =>
            s.ServiceType == typeof(IHostedService) &&
            s.ImplementationType == typeof(SchedulerHostedService));
    }

    [Fact]
    public void AddScheduling_QueueIsSingleton_JitterIsSingleton()
    {
        var services = new ServiceCollection();
        services.AddScheduling(Config());

        var queues = services.Where(s => s.ServiceType == typeof(IJobQueue)).ToList();
        Assert.Single(queues);
        Assert.Equal(ServiceLifetime.Singleton, queues[0].Lifetime);

        var jitters = services.Where(s => s.ServiceType == typeof(IJitterSource)).ToList();
        Assert.Single(jitters);
        Assert.Equal(ServiceLifetime.Singleton, jitters[0].Lifetime);
    }
}
