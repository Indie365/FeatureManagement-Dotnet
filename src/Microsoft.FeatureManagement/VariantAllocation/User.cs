﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
//

using System.Collections.Generic;

namespace Microsoft.FeatureManagement.VariantAllocation
{
    /// <summary>
    /// The definition of a user allocation.
    /// </summary>
    public class User
    {
        /// <summary>
        /// The name of the variant.
        /// </summary>
        public string Variant { get; set; }

        /// <summary>
        /// A list of users that will be assigned this variant.
        /// </summary>
        public IEnumerable<string> Users { get; set; }
    }
}