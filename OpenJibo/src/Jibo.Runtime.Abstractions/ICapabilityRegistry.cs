namespace Jibo.Runtime.Abstractions;

public interface ICapabilityRegistry
{
    TCapability? Get<TCapability>(string name) where TCapability : class, ICapability;
}