﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
//
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.FeatureManagement
{
    /// <summary>
    /// A feature definition provider that pulls feature definitions from the .NET Core <see cref="IConfiguration"/> system.
    /// </summary>
    sealed class ConfigurationFeatureDefinitionProvider : IFeatureDefinitionProvider, IDisposable, IFeatureDefinitionProviderCacheable
    {
        //
        // IFeatureDefinitionProviderCacheable interface is only used to mark this provider as cacheable. This allows our test suite's
        // provider to be marked for caching as well.

        private readonly IConfiguration _configuration;
        private readonly ConcurrentDictionary<string, FeatureDefinition> _definitions;
        private IDisposable _changeSubscription;
        private int _stale = 0;

        public ConfigurationFeatureDefinitionProvider(IConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _definitions = new ConcurrentDictionary<string, FeatureDefinition>();

            _changeSubscription = ChangeToken.OnChange(
                () => _configuration.GetReloadToken(),
                () => _stale = 1);
        }

        public void Dispose()
        {
            _changeSubscription?.Dispose();

            _changeSubscription = null;
        }

        public Task<FeatureDefinition> GetFeatureDefinitionAsync(string featureName)
        {
            if (featureName == null)
            {
                throw new ArgumentNullException(nameof(featureName));
            }

            if (Interlocked.Exchange(ref _stale, 0) != 0)
            {
                _definitions.Clear();
            }

            //
            // Query by feature name
            FeatureDefinition definition = _definitions.GetOrAdd(featureName, (name) => ReadFeatureDefinition(name));

            return Task.FromResult(definition);
        }

        //
        // The async key word is necessary for creating IAsyncEnumerable.
        // The need to disable this warning occurs when implementaing async stream synchronously. 
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public async IAsyncEnumerable<FeatureDefinition> GetAllFeatureDefinitionsAsync()
#pragma warning restore CS1998
        {
            if (Interlocked.Exchange(ref _stale, 0) != 0)
            {
                _definitions.Clear();
            }

            //
            // Iterate over all features registered in the system at initial invocation time
            foreach (IConfigurationSection featureSection in GetFeatureDefinitionSections())
            {
                //
                // Underlying IConfigurationSection data is dynamic so latest feature definitions are returned
                yield return  _definitions.GetOrAdd(featureSection.Key, (_) => ReadFeatureDefinition(featureSection));
            }
        }

        private FeatureDefinition ReadFeatureDefinition(string featureName)
        {
            IConfigurationSection configuration = GetFeatureDefinitionSections()
                                                    .FirstOrDefault(section => section.Key.Equals(featureName, StringComparison.OrdinalIgnoreCase));

            if (configuration == null)
            {
                return null;
            }

            return ReadFeatureDefinition(configuration);
        }

        private FeatureDefinition ReadFeatureDefinition(IConfigurationSection configurationSection)
        {
            /*
              
            We support
            
            myFeature: {
              enabledFor: [ "myFeatureFilter1", "myFeatureFilter2" ]
            },
            myDisabledFeature: {
              enabledFor: [  ]
            },
            myFeature2: {
              enabledFor: "myFeatureFilter1;myFeatureFilter2"
            },
            myDisabledFeature2: {
              enabledFor: ""
            },
            myFeature3: "myFeatureFilter1;myFeatureFilter2",
            myDisabledFeature3: "",
            myAlwaysEnabledFeature: true,
            myAlwaysDisabledFeature: false // removing this line would be the same as setting it to false
            myAlwaysEnabledFeature2: {
              enabledFor: true
            },
            myAlwaysDisabledFeature2: {
              enabledFor: false
            },
            myAllRequiredFilterFeature: {
                requirementType: "all"
                enabledFor: [ "myFeatureFilter1", "myFeatureFilter2" ],
            },

            */

            RequirementType requirementType = RequirementType.Any;

            FeatureStatus featureStatus = FeatureStatus.Conditional;

            Allocation allocation = null;

            List<VariantDefinition> variants = null;

            var enabledFor = new List<FeatureFilterConfiguration>();

            string val = configurationSection.Value; // configuration[$"{featureName}"];

            if (string.IsNullOrEmpty(val))
            {
                val = configurationSection[ConfigurationFields.FeatureFiltersSectionName];
            }

            if (!string.IsNullOrEmpty(val) && bool.TryParse(val, out bool result) && result)
            {
                //
                //myAlwaysEnabledFeature: true
                // OR
                //myAlwaysEnabledFeature: {
                //  enabledFor: true
                //}
                enabledFor.Add(new FeatureFilterConfiguration
                {
                    Name = "AlwaysOn"
                });
            }
            else
            {
                string rawRequirementType = configurationSection[ConfigurationFields.RequirementType];

                string rawFeatureStatus = configurationSection[ConfigurationFields.FeatureStatus];

                string parseEnumErrorString = "Invalid {0} with value '{1}' for feature '{2}'.";

                //
                // If the enum is specified, parse it and set the return value
                if (!string.IsNullOrEmpty(rawRequirementType) && !Enum.TryParse(rawRequirementType, ignoreCase: true, out requirementType))
                {
                    throw new FeatureManagementException(
                        FeatureManagementError.InvalidConfigurationSetting,
                        string.Format(parseEnumErrorString, nameof(RequirementType), rawRequirementType, configurationSection.Key));
                }

                if (!string.IsNullOrEmpty(rawFeatureStatus) && !Enum.TryParse(rawFeatureStatus, ignoreCase: true, out featureStatus))
                {
                    throw new FeatureManagementException(
                        FeatureManagementError.InvalidConfigurationSetting,
                        string.Format(parseEnumErrorString, nameof(FeatureStatus), rawFeatureStatus, configurationSection.Key));
                }

                IEnumerable<IConfigurationSection> filterSections = configurationSection.GetSection(ConfigurationFields.FeatureFiltersSectionName).GetChildren();

                foreach (IConfigurationSection section in filterSections)
                {
                    //
                    // Arrays in json such as "myKey": [ "some", "values" ]
                    // Are accessed through the configuration system by using the array index as the property name, e.g. "myKey": { "0": "some", "1": "values" }
                    if (int.TryParse(section.Key, out int _) && !string.IsNullOrEmpty(section[ConfigurationFields.NameKeyword]))
                    {
                        enabledFor.Add(new FeatureFilterConfiguration()
                        {
                            Name = section[ConfigurationFields.NameKeyword],
                            Parameters = new ConfigurationWrapper(section.GetSection(ConfigurationFields.FeatureFilterConfigurationParameters))
                        });
                    }
                }

                IConfigurationSection allocationSection = configurationSection.GetSection(ConfigurationFields.AllocationSectionName);

                if (allocationSection.Exists())
                {
                    allocation = new Allocation()
                    {
                        DefaultWhenDisabled = allocationSection[ConfigurationFields.AllocationDefaultWhenDisabled],
                        DefaultWhenEnabled = allocationSection[ConfigurationFields.AllocationDefaultWhenEnabled],
                        User = allocationSection.GetSection(ConfigurationFields.UserAllocationSectionName).GetChildren().Select(userAllocation =>
                        {
                            return new UserAllocation()
                            {
                                Variant = userAllocation[ConfigurationFields.AllocationVariantKeyword],
                                Users = userAllocation.GetSection(ConfigurationFields.UserAllocationUsers).Get<IEnumerable<string>>()
                            };
                        }),
                        Group = allocationSection.GetSection(ConfigurationFields.GroupAllocationSectionName).GetChildren().Select(groupAllocation =>
                        {
                            return new GroupAllocation()
                            {
                                Variant = groupAllocation[ConfigurationFields.AllocationVariantKeyword],
                                Groups = groupAllocation.GetSection(ConfigurationFields.GroupAllocationGroups).Get<IEnumerable<string>>()
                            };
                        }),
                        Percentile = allocationSection.GetSection(ConfigurationFields.PercentileAllocationSectionName).GetChildren().Select(percentileAllocation =>
                        {
                            return new PercentileAllocation()
                            {
                                Variant = percentileAllocation[ConfigurationFields.AllocationVariantKeyword],
                                From = percentileAllocation.GetValue<double>(ConfigurationFields.PercentileAllocationFrom),
                                To = percentileAllocation.GetValue<double>(ConfigurationFields.PercentileAllocationTo)
                            };
                        }),
                        Seed = allocationSection[ConfigurationFields.AllocationSeed]
                    };
                }

                IEnumerable<IConfigurationSection> variantsSections = configurationSection.GetSection(ConfigurationFields.VariantsSectionName).GetChildren();
                variants = new List<VariantDefinition>();

                foreach (IConfigurationSection section in variantsSections)
                {
                    if (int.TryParse(section.Key, out int _) && !string.IsNullOrEmpty(section[ConfigurationFields.NameKeyword]))
                    {
                        VariantDefinition variant = new VariantDefinition()
                        {
                            Name = section[ConfigurationFields.NameKeyword],
                            ConfigurationValue = section.GetSection(ConfigurationFields.VariantDefinitionConfigurationValue),
                            ConfigurationReference = section[ConfigurationFields.VariantDefinitionConfigurationReference],
                            StatusOverride = section.GetValue<StatusOverride>(ConfigurationFields.VariantDefinitionStatusOverride)
                        };
                        variants.Add(variant);
                    }
                }
            }

            return new FeatureDefinition()
            {
                Name = configurationSection.Key,
                EnabledFor = enabledFor,
                RequirementType = requirementType,
                Status = featureStatus,
                Allocation = allocation,
                Variants = variants
            };
        }

        private IEnumerable<IConfigurationSection> GetFeatureDefinitionSections()
        {
            if (_configuration.GetChildren().Any(s => s.Key.Equals(ConfigurationFields.FeatureManagementSectionName, StringComparison.OrdinalIgnoreCase)))
            {
                //
                // Look for feature definitions under the "FeatureManagement" section
                return _configuration.GetSection(ConfigurationFields.FeatureManagementSectionName).GetChildren();
            }
            else
            {
                return _configuration.GetChildren();
            }
        }
    }
}
