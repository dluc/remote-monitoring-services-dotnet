﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Devices;
using Microsoft.Azure.IoTSolutions.UIConfig.Services.Exceptions;
using Microsoft.Azure.IoTSolutions.UIConfig.Services.External;
using Microsoft.Azure.IoTSolutions.UIConfig.Services.Helpers.PackageValidation;
using Microsoft.Azure.IoTSolutions.UIConfig.Services.Models;
using Microsoft.Azure.IoTSolutions.UIConfig.Services.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.IoTSolutions.UIConfig.Services
{
    public interface IStorage
    {
        Task<object> GetThemeAsync();
        Task<object> SetThemeAsync(object theme);
        Task<object> GetUserSetting(string id);
        Task<object> SetUserSetting(string id, object setting);
        Task<Logo> GetLogoAsync();
        Task<Logo> SetLogoAsync(Logo model);
        Task<IEnumerable<DeviceGroup>> GetAllDeviceGroupsAsync();
        Task<ConfigTypeList> GetAllConfigurationsAsync();
        Task<DeviceGroup> GetDeviceGroupAsync(string id);
        Task<DeviceGroup> CreateDeviceGroupAsync(DeviceGroup input);
        Task<DeviceGroup> UpdateDeviceGroupAsync(string id, DeviceGroup input, string etag);
        Task DeleteDeviceGroupAsync(string id);
        Task<IEnumerable<Package>> GetPackagesAsync();
        Task<Package> GetPackageAsync(string id);
        Task<Package> AddPackageAsync(Package package);
        Task DeletePackageAsync(string id);
        Task UpdateConfigurationsAsync(string customConfigType);
    }

    public class Storage : IStorage
    {
        private readonly IStorageAdapterClient client;
        private readonly IServicesConfig config;

        internal const string SOLUTION_COLLECTION_ID = "solution-settings";
        internal const string THEME_KEY = "theme";
        internal const string LOGO_KEY = "logo";
        internal const string USER_COLLECTION_ID = "user-settings";
        internal const string DEVICE_GROUP_COLLECTION_ID = "devicegroups";
        internal const string PACKAGES_COLLECTION_ID = "packages";
        internal const string PACKAGES_CONFIGURATION_TYPE_KEY = "Items";
        private const string AZURE_MAPS_KEY = "AzureMapsKey";
        private const string DATE_FORMAT = "yyyy-MM-dd'T'HH:mm:sszzz";

        public Storage(
            IStorageAdapterClient client,
            IServicesConfig config)
        {
            this.client = client;
            this.config = config;
        }

        public async Task<object> GetThemeAsync()
        {
            string data;

            try
            {
                var response = await this.client.GetAsync(SOLUTION_COLLECTION_ID, THEME_KEY);
                data = response.Data;
            }
            catch (ResourceNotFoundException)
            {
                data = JsonConvert.SerializeObject(Theme.Default);
            }

            var themeOut = JsonConvert.DeserializeObject(data) as JToken ?? new JObject();
            this.AppendAzureMapsKey(themeOut);
            return themeOut;
        }

        public async Task<object> SetThemeAsync(object themeIn)
        {
            var value = JsonConvert.SerializeObject(themeIn);
            var response = await this.client.UpdateAsync(SOLUTION_COLLECTION_ID, THEME_KEY, value, "*");
            var themeOut = JsonConvert.DeserializeObject(response.Data) as JToken ?? new JObject();
            this.AppendAzureMapsKey(themeOut);
            return themeOut;
        }

        private void AppendAzureMapsKey(JToken theme)
        {
            if (theme[AZURE_MAPS_KEY] == null)
            {
                theme[AZURE_MAPS_KEY] = this.config.AzureMapsKey;
            }
        }

        public async Task<object> GetUserSetting(string id)
        {
            try
            {
                var response = await this.client.GetAsync(USER_COLLECTION_ID, id);
                return JsonConvert.DeserializeObject(response.Data);
            }
            catch (ResourceNotFoundException)
            {
                return new object();
            }
        }

        public async Task<object> SetUserSetting(string id, object setting)
        {
            var value = JsonConvert.SerializeObject(setting);
            var response = await this.client.UpdateAsync(USER_COLLECTION_ID, id, value, "*");
            return JsonConvert.DeserializeObject(response.Data);
        }

        public async Task<Logo> GetLogoAsync()
        {
            try
            {
                var response = await this.client.GetAsync(SOLUTION_COLLECTION_ID, LOGO_KEY);
                return JsonConvert.DeserializeObject<Logo>(response.Data);
            }
            catch (ResourceNotFoundException)
            {
                return Logo.Default;
            }
        }

        public async Task<Logo> SetLogoAsync(Logo model)
        {
            //Do not overwrite existing name or image with null
            if(model.Name == null || model.Image == null)
            {
                Logo current = await this.GetLogoAsync();
                if(!current.IsDefault)
                {
                    model.Name = model.Name ?? current.Name;
                    if (model.Image == null && current.Image != null)
                    {
                        model.Image = current.Image;
                        model.Type = current.Type;
                    }
                }
            }

            var value = JsonConvert.SerializeObject(model);
            var response = await this.client.UpdateAsync(SOLUTION_COLLECTION_ID, LOGO_KEY, value, "*");
            return JsonConvert.DeserializeObject<Logo>(response.Data);
        }

        public async Task<IEnumerable<DeviceGroup>> GetAllDeviceGroupsAsync()
        {
            var response = await this.client.GetAllAsync(DEVICE_GROUP_COLLECTION_ID);
            return response.Items.Select(this.CreateGroupServiceModel);
        }

        public async Task<DeviceGroup> GetDeviceGroupAsync(string id)
        {
            var response = await this.client.GetAsync(DEVICE_GROUP_COLLECTION_ID, id);
            return this.CreateGroupServiceModel(response);
        }

        public async Task<DeviceGroup> CreateDeviceGroupAsync(DeviceGroup input)
        {
            var value = JsonConvert.SerializeObject(input, Formatting.Indented, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            var response = await this.client.CreateAsync(DEVICE_GROUP_COLLECTION_ID, value);
            return this.CreateGroupServiceModel(response);
        }

        public async Task<DeviceGroup> UpdateDeviceGroupAsync(string id, DeviceGroup input, string etag)
        {
            var value = JsonConvert.SerializeObject(input, Formatting.Indented, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
            var response = await this.client.UpdateAsync(DEVICE_GROUP_COLLECTION_ID, id, value, etag);
            return this.CreateGroupServiceModel(response);
        }

        public async Task DeleteDeviceGroupAsync(string id)
        {
            await this.client.DeleteAsync(DEVICE_GROUP_COLLECTION_ID, id);
        }

        public async Task<IEnumerable<Package>> GetPackagesAsync()
        {
            var response = await this.client.GetAllAsync(PACKAGES_COLLECTION_ID);
            return response.Items.AsParallel().Select(this.CreatePackageServiceModel);
        }

        public async Task<Package> AddPackageAsync(Package package)
        {
            bool isValidPackage = ValidatePackage(package);
            if (!isValidPackage)
            {
                throw new InvalidInputException($"Package provided is not a valid deployment manifest " +
                    $"for type {package.packageType} and config type {package.ConfigType}");
            }

            try
            {
                JsonConvert.DeserializeObject<Configuration>(package.Content);
            }
            catch (Exception)
            {
                throw new InvalidInputException("Package provided is not a valid deployment manifest");
            }

            package.DateCreated = DateTimeOffset.UtcNow.ToString(DATE_FORMAT);
            var value = JsonConvert.SerializeObject(package,
                                                    Formatting.Indented,
                                                    new JsonSerializerSettings {
                                                        NullValueHandling = NullValueHandling.Ignore
                                                    });

            var response = await this.client.CreateAsync(PACKAGES_COLLECTION_ID, value);
            
            if (!Enum.GetNames(typeof(ConfigType)).Contains(package.ConfigType)
                && package.packageType.Equals(PackageType.DeviceConfiguration))
            {
                await this.UpdateConfigurationsAsync(package.ConfigType);
            }

            return this.CreatePackageServiceModel(response);
        }

        private Boolean ValidatePackage(Package package)
        {
            IPackageValidator validator = PackageValidatorFactory.GetValidator(package.packageType, package.ConfigType);
            if (validator == null)
            {
                return true;//Bypass validation for custom config type
            }
            return validator.Validate();
        }

        public async Task DeletePackageAsync(string id)
        {
            await this.client.DeleteAsync(PACKAGES_COLLECTION_ID, id);
        }

        public async Task<Package> GetPackageAsync(string id)
        {
            var response = await this.client.GetAsync(PACKAGES_COLLECTION_ID, id);
            return this.CreatePackageServiceModel(response);
        }

        public async Task<ConfigTypeList> GetAllConfigurationsAsync()
        {
            try
            {
                var response = await this.client.GetAsync(PACKAGES_COLLECTION_ID, PACKAGES_CONFIGURATION_TYPE_KEY);
                return JsonConvert.DeserializeObject<ConfigTypeList>(response.Data);
            }
            catch (ResourceNotFoundException)
            {
                return new ConfigTypeList(); //Return empty Package Configurations 
            }
        }

        public async Task UpdateConfigurationsAsync(string customConfigType)
        {
            ConfigTypeList list;
            try
            {
                var response = await this.client.GetAsync(PACKAGES_COLLECTION_ID, PACKAGES_CONFIGURATION_TYPE_KEY);
                list = JsonConvert.DeserializeObject<ConfigTypeList>(response.Data);
            }
            catch (ResourceNotFoundException) 
            {
                list = new ConfigTypeList();
            }
            list.add(customConfigType);
            await this.client.UpdateAsync(PACKAGES_COLLECTION_ID, PACKAGES_CONFIGURATION_TYPE_KEY, JsonConvert.SerializeObject(list), "*");
        }

        private DeviceGroup CreateGroupServiceModel(ValueApiModel input)
        {
            var output = JsonConvert.DeserializeObject<DeviceGroup>(input.Data);
            output.Id = input.Key;
            output.ETag = input.ETag;
            return output;
        }

        private Package CreatePackageServiceModel(ValueApiModel input)
        {
            var output = JsonConvert.DeserializeObject<Package>(input.Data);
            output.Id = input.Key;
            return output;
        }
    }
}
