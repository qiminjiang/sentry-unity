using System;
using UnityEngine;
using Sentry.Unity;

[CreateAssetMenu(fileName = "Assets/Resources/Sentry/SentryRuntimeOptionsConfiguration.asset", menuName = "Sentry/Assets/Resources/Sentry/SentryRuntimeOptionsConfiguration.asset", order = 999)]
public class SentryRuntimeOptionsConfiguration : Sentry.Unity.ScriptableOptionsConfiguration
{
    /// See base class for documentation.
    /// Learn more at https://docs.sentry.io/platforms/unity/configuration/options/#programmatic-configuration
    public override void Configure(SentryUnityOptions options)
    {
        Debug.Log("SentryRuntimeOptionsConfiguration::Configure() called");

        /// BeforeSend is only relevant at runtime. It wouldn't hurt to be set at build time, just wouldn't do anything.
        options.BeforeSend = sentryEvent =>
        {
            if (sentryEvent.Tags.ContainsKey("SomeTag"))
            {
                // Don't send events with a tag SomeTag to Sentry
                return null;
            }

            return sentryEvent;
        };

        Debug.Log("SentryRuntimeOptionsConfiguration::Configure() finished");
    }
}