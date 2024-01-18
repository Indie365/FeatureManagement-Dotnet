﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
//
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.FeatureManagement
{
    internal class FeaturedService<TService> : IFeaturedService<TService> where TService : class
    {
        private readonly string _featureName;
        private readonly IVariantFeatureManager _featureManager;
        private readonly IEnumerable<TService> _services;

        public FeaturedService(string featureName, IEnumerable<TService> services, IVariantFeatureManager featureManager)
        {
            _featureName = featureName;
            _services = services;
            _featureManager = featureManager;
        }

        public async ValueTask<TService> GetAsync(CancellationToken cancellationToken)
        {
            Variant variant = await _featureManager.GetVariantAsync(_featureName, cancellationToken);

            TService implementation = null;

            if (variant != null)
            {
                implementation = _services.FirstOrDefault(service => 
                    IsMatchingVariant(
                        service.GetType(),
                        variant));
            }

            return implementation;
        }

        private bool IsMatchingVariant(Type implementationType, Variant variant)
        {
            Debug.Assert(variant != null);

            string implementationName = ((FeaturedServiceAliasAttribute)Attribute.GetCustomAttribute(implementationType, typeof(FeaturedServiceAliasAttribute)))?.Alias;

            if (implementationName == null)
            {
                implementationName = implementationType.Name;
            }

            string variantConfiguration = variant.Configuration?.Value;

            if (variantConfiguration != null && string.Equals(implementationName, variantConfiguration, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(implementationName, variant.Name, StringComparison.OrdinalIgnoreCase);
        }
    }
}