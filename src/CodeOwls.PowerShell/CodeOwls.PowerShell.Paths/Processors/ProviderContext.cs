/*
	Copyright (c) 2014 Code Owls LLC

	Permission is hereby granted, free of charge, to any person obtaining a copy
	of this software and associated documentation files (the "Software"), to
	deal in the Software without restriction, including without limitation the
	rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
	sell copies of the Software, and to permit persons to whom the Software is
	furnished to do so, subject to the following conditions:

	The above copyright notice and this permission notice shall be included in
	all copies or substantial portions of the Software.

	THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
	IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
	FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
	AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
	LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
	FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
	IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Provider;
using System.Security.AccessControl;
using CodeOwls.PowerShell.Paths.Processors;
using CodeOwls.PowerShell.Provider.PathNodes;

namespace CodeOwls.PowerShell.Provider.PathNodeProcessors
{
    public class ProviderContext : IProviderContext
    {
        private readonly string path;
        private readonly CmdletProvider provider;
        private PSDriveInfo drive;
        private readonly bool recurse;
        private IPathResolver pathProcessor;

        public ProviderContext(CmdletProvider provider, string path, PSDriveInfo drive, IPathResolver pathProcessor, object dynamicParameters)
            : this(provider, path, drive, pathProcessor, dynamicParameters, new Version(1, 0))
        {
        }

        public ProviderContext(CmdletProvider provider, string path, PSDriveInfo drive, IPathResolver pathProcessor, object dynamicParameters, Version topology)
            : this(provider, path, drive, pathProcessor, dynamicParameters, topology, false)
        {
        }

        public ProviderContext(CmdletProvider provider, string path, PSDriveInfo drive, IPathResolver pathProcessor, object dynamicParameters, bool recurse)
            : this(provider, path, drive, pathProcessor, dynamicParameters, new Version(1, 0), recurse)
        {
        }

        public ProviderContext(CmdletProvider provider, string path, PSDriveInfo drive, IPathResolver pathProcessor, object dynamicParameters, Version topology, bool recurse)
        {
            this.pathProcessor = pathProcessor;
            DynamicParameters = dynamicParameters;
            this.provider = provider;
            this.path = path;
            this.drive = drive;
            this.recurse = recurse;
            PathTopologyVersion = topology;
        }

        public ProviderContext(IProviderContext providerContext, object dynamicParameters)
        {
            ProviderContext c = providerContext as ProviderContext;
            if (null == c)
            {
                throw new ArgumentException("the providerContext provided is of an incompatible type");
            }

            provider = c.provider;
            drive = c.drive;
            path = c.path;
            pathProcessor = c.pathProcessor;
            DynamicParameters = dynamicParameters;
            recurse = c.Recurse;
            PathTopologyVersion = c.PathTopologyVersion;
        }

        public bool ResolveFinalNodeFilterItems { get; set; }

        public IPathNode ResolvePath(string path)
        {
            var items = pathProcessor.ResolvePath(this, path);
            return items?.First();
        }

        public PSDriveInfo Drive
        {
            get { return drive; }
        }

        public string GetResourceString(string baseName, string resourceId)
        {
            return provider.GetResourceString(baseName, resourceId);
        }

        public void ThrowTerminatingError(ErrorRecord errorRecord)
        {
            provider.ThrowTerminatingError(errorRecord);
        }

        public bool ShouldProcess(string target)
        {
            return provider.ShouldProcess(target);
        }

        public bool ShouldProcess(string target, string action)
        {
            return provider.ShouldProcess(target, action);
        }

        public bool ShouldProcess(string verboseDescription, string verboseWarning, string caption)
        {
            return provider.ShouldProcess(verboseDescription, verboseWarning, caption);
        }

        public bool ShouldProcess(string verboseDescription, string verboseWarning, string caption, out ShouldProcessReason shouldProcessReason)
        {
            return provider.ShouldProcess(verboseDescription, verboseWarning, caption, out shouldProcessReason);
        }

        public bool ShouldContinue(string query, string caption)
        {
            return provider.ShouldContinue(query, caption);
        }

        public bool ShouldContinue(string query, string caption, ref bool yesToAll, ref bool noToAll)
        {
            return provider.ShouldContinue(query, caption, ref yesToAll, ref noToAll);
        }

        public bool TransactionAvailable()
        {
            return provider.TransactionAvailable();
        }

        public void WriteVerbose(string text)
        {
            provider.WriteVerbose(text);
        }

        public void WriteWarning(string text)
        {
            provider.WriteWarning(text);
        }

        public void WriteProgress(ProgressRecord progressRecord)
        {
            provider.WriteProgress(progressRecord);
        }

        public void WriteDebug(string text)
        {
            provider.WriteDebug(text);
        }

        public void WriteItemObject(object item, string path, bool isContainer)
        {
            provider.WriteItemObject(item, path, isContainer);
        }

        public void WritePropertyObject(object propertyValue, string path)
        {
            provider.WritePropertyObject(propertyValue, path);
        }

        public void WriteSecurityDescriptorObject(ObjectSecurity securityDescriptor, string path)
        {
            provider.WriteSecurityDescriptorObject(securityDescriptor, path);
        }

        public void WriteError(ErrorRecord errorRecord)
        {
            provider.WriteError(errorRecord);
        }

        public bool Stopping
        {
            get { return provider.Stopping; }
        }

        public SessionState SessionState
        {
            get { return provider.SessionState; }
        }

        public ProviderIntrinsics InvokeProvider
        {
            get { return provider.InvokeProvider; }
        }

        public CommandInvocationIntrinsics InvokeCommand
        {
            get { return provider.InvokeCommand; }
        }

        public PSCredential Credential
        {
            get { return provider.Credential; }
        }

        public bool Force
        {
            get { return provider.Force.IsPresent; }
        }

        public bool Recurse
        {
            get { return recurse; }
        }

        public string Filter
        {
            get { return provider.Filter; }
        }

        public IEnumerable<string> Include
        {
            get { return provider.Include; }
        }

        public IEnumerable<string> Exclude
        {
            get { return provider.Exclude; }
        }

        public object DynamicParameters { get; }

        public Version PathTopologyVersion { get; set; }

        public string Path { get { return path; } }
    }
}