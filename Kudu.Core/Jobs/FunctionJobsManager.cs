using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Kudu.Contracts.Jobs;
using Kudu.Core.Tracing;
using Kudu.Contracts.Settings;

namespace Kudu.Core.Jobs
{
    public class FunctionJobsManager : JobsManagerBase<ContinuousJob>, IDisposable
    {
        private JobsFileWatcher _jobsFileWatcher;
        private ContinuousJobRunner _hostJobRunner;
        public FunctionJobsManager(ITraceFactory traceFactory, IEnvironment environment, IDeploymentSettingsManager settings, IAnalytics analytics)
            : base(traceFactory, environment, settings, analytics, string.Empty)
        {
            _jobsFileWatcher = new JobsFileWatcher(JobsBinariesPath, OnHostChange, Constants.FunctionsHostConfigFile, () => new[] { Constants.Functions }, traceFactory, analytics);
        }

        private void OnHostChange(string name)
        {
            var hostJob = GetJob(Constants.Functions);
            if (hostJob == null || !string.IsNullOrEmpty(hostJob.Error))
            {
                //remove job
                if (_hostJobRunner != null)
                {
                    _hostJobRunner.StopJob();
                    _hostJobRunner.Dispose();
                    _hostJobRunner = null;
                }
            }
            else
            {
                //refersh job
                if (_hostJobRunner == null)
                {
                    _hostJobRunner = new ContinuousJobRunner(hostJob, Environment, Settings, TraceFactory, Analytics, string.Empty);
                }
                if (_jobsFileWatcher.FirstTimeMakingChanges)
                {
                    _hostJobRunner.RefreshJob(hostJob, hostJob.Settings, logRefresh: !_jobsFileWatcher.FirstTimeMakingChanges);
                }
            }
        }

        public override ContinuousJob GetJob(string jobName)
        {
            if (!jobName.Equals(Constants.Functions, StringComparison.OrdinalIgnoreCase)) return null;
            var job = GetJobInternal(jobName);
            job.Settings = new JobSettings();
            job.Settings[JobSettingsKeys.IsInPlace] = true;
            return job;
        }

        public override IEnumerable<ContinuousJob> ListJobs()
        {
            var hostJob = GetJob(Constants.Functions);
            return hostJob != null ? new[] { hostJob } : Enumerable.Empty<ContinuousJob>();
        }

        protected override void OnShutdown()
        {
            _jobsFileWatcher.Stop();
            _hostJobRunner.StopJob(isShutdown: true);
        }

        protected override void UpdateJob(ContinuousJob job)
        {
            //no-op
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            // HACK: Next if statement should be removed once ninject wlll not dispose this class
            // Since ninject automatically calls dispose we currently disable it
            if (disposing)
            {
                return;
            }
            // End of code to be removed

            if (disposing)
            {
                if (_jobsFileWatcher != null)
                {
                    _jobsFileWatcher.Dispose();
                    _jobsFileWatcher = null;
                }

                if (_hostJobRunner != null)
                {
                    _hostJobRunner.Dispose();
                    _hostJobRunner = null;
                }
            }
        }
    }
}
