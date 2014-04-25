#region Copyright (C) 2005-2011 Team MediaPortal

// Copyright (C) 2005-2011 Team MediaPortal
// http://www.team-mediaportal.com
// 
// MediaPortal is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 2 of the License, or
// (at your option) any later version.
// 
// MediaPortal is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
// 
// You should have received a copy of the GNU General Public License
// along with MediaPortal. If not, see <http://www.gnu.org/licenses/>.

#endregion

using System;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Castle.Core;
using Ionic.Zip;
using MediaPortal.Common.Utils;
using Mediaportal.TV.Server.Plugins.Base.Interfaces;
using Mediaportal.TV.Server.Plugins.PowerScheduler.Interfaces.Interfaces;
using Mediaportal.TV.Server.Plugins.XmlTvImport.util;
using Mediaportal.TV.Server.SetupControls;
using Mediaportal.TV.Server.TVControl.Interfaces.Services;
using Mediaportal.TV.Server.TVDatabase.Entities;
using Mediaportal.TV.Server.TVDatabase.TVBusinessLayer;
using Mediaportal.TV.Server.TVLibrary.Interfaces;
using Mediaportal.TV.Server.TVLibrary.Interfaces.Logging;

namespace Mediaportal.TV.Server.Plugins.XmlTvImport
{
  [Interceptor("PluginExceptionInterceptor")]
  public class XmlTvImporter : ITvServerPlugin, ITvServerPluginStartedAll, ITvServerPluginCommunciation
  {


    #region constants        

    private const int remoteFileDonwloadTimeoutSecs = 360; //6 minutes

    #endregion

    #region variables

    private bool _workerThreadRunning = false;
    private bool _remoteFileDownloadInProgress = false;
    private DateTime _remoteFileDonwloadInProgressAt = DateTime.MinValue;
    private string _remoteURL = "";
    private System.Timers.Timer _timer1;

    #endregion

    #region Constructor

    /// <summary>
    /// Create a new instance of a generic standby handler
    /// </summary>
    public XmlTvImporter() {}

    #endregion

    #region properties

    /// <summary>
    /// returns the name of the plugin
    /// </summary>
    public string Name
    {
      get { return "XmlTv"; }
    }

    /// <summary>
    /// returns the version of the plugin
    /// </summary>
    public string Version
    {
      get { return "1.0.0.0"; }
    }

    /// <summary>
    /// returns the author of the plugin
    /// </summary>
    public string Author
    {
      get { return "Frodo"; }
    }

    /// <summary>
    /// returns if the plugin should only run on the master server
    /// or also on slave servers
    /// </summary>
    public bool MasterOnly
    {
      get { return true; }
    }

    public static string DefaultOutputFolder
    {
      get { return PathManager.GetDataPath + @"\xmltv"; }
    }

    #endregion

    #region public methods

    /// <summary>
    /// Starts the plugin
    /// </summary>
    public void Start(IInternalControllerService controllerService)
    {      
      this.LogDebug("plugin: xmltv started");

      //System.Diagnostics.Debugger.Launch();      
      RegisterPowerEventHandler();
      RetrieveRemoteTvGuideOnStartUp();
      CheckNewTVGuide();
      _timer1 = new System.Timers.Timer();
      _timer1.Interval = 60000;
      _timer1.Enabled = true;
      _timer1.Elapsed += new System.Timers.ElapsedEventHandler(_timer1_Elapsed);
    }


    /// <summary>
    /// Stops the plugin
    /// </summary>
    public void Stop()
    {
      this.LogDebug("plugin: xmltv stopped");

      UnRegisterPowerEventHandler();

      if (_timer1 != null)
      {
        _timer1.Enabled = false;
        _timer1.Dispose();
        _timer1 = null;
      }
    }

    private void RegisterPowerEventHandler()
    {
      // register to power events generated by the system
      if (GlobalServiceProvider.Instance.IsRegistered<IPowerEventHandler>())
      {
        GlobalServiceProvider.Instance.Get<IPowerEventHandler>().AddPowerEventHandler(new PowerEventHandler(OnPowerEvent));
        this.LogDebug("xmltv: Registered xmltv as PowerEventHandler to tvservice");
      }
      else
      {
        this.LogError("xmltv: Unable to register power event handler!");
      }
    }

    private void UnRegisterPowerEventHandler()
    {
      // unregister to power events generated by the system
      if (GlobalServiceProvider.Instance.IsRegistered<IPowerEventHandler>())
      {
        GlobalServiceProvider.Instance.Get<IPowerEventHandler>().RemovePowerEventHandler(
          new PowerEventHandler(OnPowerEvent));
        this.LogDebug("xmltv: UnRegistered xmltv as PowerEventHandler to tvservice");
      }
      else
      {
        this.LogError("xmltv: Unable to unregister power event handler!");
      }
    }

    /// <summary>
    /// Windows PowerEvent handler
    /// </summary>
    /// <param name="powerStatus">PowerBroadcastStatus the system is changing to</param>
    /// <returns>bool indicating if the broadcast was honoured</returns>
    [MethodImpl(MethodImplOptions.Synchronized)]
    private bool OnPowerEvent(PowerEventType powerStatus)
    {
      switch (powerStatus)
      {
        case PowerEventType.ResumeAutomatic:
        case PowerEventType.ResumeCritical:
        case PowerEventType.ResumeSuspend:
          // note: this event may not arrive unless the user has moved the mouse or hit a key
          // so, we should also handle ResumeAutomatic and ResumeCritical (as done above)
          Resume();
          break;
      }
      return true;
    }

    private void RetrieveRemoteTvGuideOnStartUp()
    {
      if (_remoteFileDownloadInProgress)
      {
        return;
      }

      bool downloadGuideOnWakeUp = SettingsManagement.GetValue("xmlTvRemoteSchedulerDownloadOnWakeUpEnabled", false);

      if (downloadGuideOnWakeUp)
      {
        DateTime lastTransfer = SettingsManagement.GetValue("xmlTvRemoteScheduleLastTransfer", DateTime.MinValue);

        //lastTime = DateTime.Parse(SettingsManagement.Get("xmlTvLastUpdate", "").Value);
        TimeSpan ts = DateTime.Now - lastTransfer;

        if (ts.TotalMinutes > 1440) //1440 mins = 1 day. - we only want to update once per day.
        {
          string folder = SettingsManagement.GetValue("xmlTv", DefaultOutputFolder);
          string URL = SettingsManagement.GetValue("xmlTvRemoteURL", "");
          this.LogDebug("downloadGuideOnWakeUp");
          RetrieveRemoteFile(folder, URL);
        }
      }
    }

    private void Resume()
    {
      try
      {
        this.LogInfo("xmlTV plugin resumed");
        RetrieveRemoteTvGuideOnStartUp();
      }
      catch (Exception e)
      {
        this.LogInfo("xmlTV plugin resume exception [" + e.Message + "]");
      }
    }

    /// <summary>
    /// returns the setup sections for display in SetupTv
    /// </summary>
    public SectionSettings Setup
    {
      get { return new XmlTvSetup(); }
    }

    private void DownloadFileCallback(object sender, DownloadDataCompletedEventArgs e)
    {
      //System.Diagnostics.Debugger.Launch();
      try
      {
        
        string info = "";
        byte[] result = null;

        try
        {
          if (e.Result != null || e.Result.Length > 0)
          {
            result = e.Result;
          }
        }
        catch (Exception ex)
        {
          info = "Download failed: (" + ex.InnerException.Message + ").";
        }

        if (result != null)
        {
          if (result.Length == 0)
          {
            info = "File empty.";
          }
          else
          {
            info = "File downloaded.";

            if (_remoteURL.Length == 0)
            {
              return;
            }

            Uri uri = new Uri(_remoteURL);
            string filename = uri.GetComponents(UriComponents.Path, UriFormat.SafeUnescaped);
            filename = filename.ToLower().Trim();

            bool isZip = (filename.IndexOf(".zip") > -1);
            bool isTvGuide = (filename.IndexOf("tvguide.xml") > -1);

            FileInfo fI = new FileInfo(filename);
            filename = fI.Name;

            //check if file can be opened for writing....																		
            string path = SettingsManagement.GetValue("xmlTv", "");

            if (isTvGuide || isZip)
            {
              path = path + @"\" + filename;
            }
            else
            {
              path = path + @"\tvguide.xml";
            }

            bool waitingForFileAccess = true;
            int retries = 0;
            bool fileWritten = false;
            //in case the destination file is locked by another process, retry each 30 secs, but max 5 min. before giving up
            while (waitingForFileAccess && retries < 10)
            {
              if (!_remoteFileDownloadInProgress)
              {
                return;
              }
              try
              {
                //IOUtil.CheckFileAccessRights(path, FileMode.Open, FileAccess.Write, FileShare.Write);
                if (!fileWritten)
                {
                  using (var fs = new FileStream(path, FileMode.Create))
                  {
                    fs.Write(e.Result, 0, e.Result.Length);
                    fileWritten = true;
                  }
                }
              }
              catch (Exception ex)
              {
                this.LogInfo("file is locked, retrying in 30secs. [" + ex.Message + "]");
                retries++;
                Thread.Sleep(30000); //wait 30 sec. before retrying.
              }

              if (isZip)
              {
                try
                {
                  string newLoc = SettingsManagement.GetValue("xmlTv", "") + @"\";
                  this.LogInfo("extracting zip file {0} to location {1}", path, newLoc);
                  ZipFile zip = new ZipFile(path);
                  zip.ExtractAll(newLoc, ExtractExistingFileAction.OverwriteSilently);
                }
                catch (Exception ex2)
                {
                  this.LogInfo("file is locked, retrying in 30secs. [" + ex2.Message + "]");
                  retries++;
                  Thread.Sleep(30000); //wait 30 sec. before retrying.
                }
              }

              waitingForFileAccess = false;
            }

            if (waitingForFileAccess)
            {
              info = "Trouble writing to file.";
            }
          }
        }
        SettingsManagement.SaveValue("xmlTvRemoteScheduleTransferStatus", info);
        SettingsManagement.SaveValue("xmlTvRemoteScheduleLastTransfer", DateTime.Now);        

        this.LogInfo(info);
      }
      catch (Exception) {}
      finally
      {
        _remoteFileDownloadInProgress = false; //signal that we are done downloading.
        SetStandbyAllowed(true);
      }
    }

    public void RetrieveRemoteFile(String folder, string URL)
    {
      //System.Diagnostics.Debugger.Launch();			
      if (_remoteFileDownloadInProgress)
      {
        return;
      }
      string lastTransferAt = "";
      string transferStatus = "";

      _remoteURL = URL;
      
      Setting setting;

      string errMsg = "";
      if (URL.Length == 0)
      {
        errMsg = "No URL defined.";
        this.LogError(errMsg);
        SettingsManagement.SaveValue("xmlTvRemoteScheduleTransferStatus", errMsg);
        
        _remoteFileDownloadInProgress = false;
        SetStandbyAllowed(true);
        return;
      }

      if (folder.Length == 0)
      {
        errMsg = "No tvguide.xml path defined.";
        this.LogError(errMsg);
        SettingsManagement.SaveValue("xmlTvRemoteScheduleTransferStatus", errMsg);        
        _remoteFileDownloadInProgress = false;
        SetStandbyAllowed(true);
        return;
      }

      lastTransferAt = DateTime.Now.ToString();
      transferStatus = "downloading...";

      using (var webClient = new WebClient())
      {
        bool isFTP = (URL.ToLowerInvariant().IndexOf("ftp://") == 0);

        if (isFTP)
        {
          // grab username, password and server from the URL
          // ftp://user:pass@www.somesite.com/TVguide.xml

          this.LogInfo("FTP URL detected.");

          int passwordEndIdx = URL.IndexOf("@");

          if (passwordEndIdx > -1)
          {
            this.LogInfo("FTP username/password detected.");

            int userStartIdx = 6; //6 is the length of chars in  --> "ftp://"
            int userEndIdx = URL.IndexOf(":", userStartIdx);

            string user = URL.Substring(userStartIdx, (userEndIdx - userStartIdx));
            string pass = URL.Substring(userEndIdx + 1, (passwordEndIdx - userEndIdx - 1));
            URL = "ftp://" + URL.Substring(passwordEndIdx + 1);

            webClient.Credentials = new NetworkCredential(user, pass);
            webClient.Proxy.Credentials = CredentialCache.DefaultCredentials;
          }
          else
          {
            this.LogInfo("no FTP username/password detected. Using anonymous access.");
          }
        }
        else
        {
          this.LogInfo("HTTP URL detected.");
        }

        this.LogInfo("initiating download of remote file from " + URL);
        Uri uri = new Uri(URL);
        webClient.DownloadDataCompleted += new DownloadDataCompletedEventHandler(DownloadFileCallback);

        try
        {
          SetStandbyAllowed(false);
          _remoteFileDownloadInProgress = true;
          _remoteFileDonwloadInProgressAt = DateTime.Now;
          webClient.DownloadDataAsync(uri);
        }
        catch (WebException ex)
        {
          errMsg = "An error occurred while downloading the file: " + URL + " (" + ex.Message + ").";
          this.LogError(errMsg);
          lastTransferAt = errMsg;
        }
        catch (InvalidOperationException ex)
        {
          errMsg = "The " + folder + @"\tvguide.xml file is in use by another thread (" + ex.Message + ").";
          this.LogError(errMsg);
          lastTransferAt = errMsg;
        }
        catch (Exception ex)
        {
          errMsg = "Unknown error @ " + URL + "(" + ex.Message + ").";
          this.LogError(errMsg);
          lastTransferAt = errMsg;
        }
      }
      SettingsManagement.SaveValue("xmlTvRemoteScheduleTransferStatus", transferStatus);
      SettingsManagement.SaveValue("xmlTvRemoteScheduleLastTransfer", lastTransferAt);
    }

    /// <summary>
    /// Forces the import of the tvguide. Usable when testing the grabber
    /// </summary>
    /// <param name="folder">The folder where tvguide.xml or tvguide.lst is stored</param>
    /// <param name="importXML">True to import tvguide.xml</param>
    /// <param name="importLST">True to import files in tvguide.lst</param>
    public void ForceImport(string folder, bool importXML, bool importLST)
    {     
      string fileName = folder + @"\tvguide.xml";

      if (System.IO.File.Exists(fileName) && importXML)
      {
        importXML = true;
      }

      fileName = folder + @"\tvguide.lst";

      if (importLST && System.IO.File.Exists(fileName))
      {
        DateTime fileTime = DateTime.Parse(System.IO.File.GetLastWriteTime(fileName).ToString());
        // for rounding errors!!!
        importLST = true;
      }

      if (importXML || importLST)
      {
        var tp = new ThreadParams {_importDate = DateTime.MinValue, _importLST = importLST, _importXML = importXML};
        ThreadFunctionImportTVGuide(tp);        
      }
    }

    #endregion

    #region private members

    private void _timer1_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
    {
      RetrieveRemoteTvGuide();

      DateTime now = DateTime.Now;

      if (_remoteFileDownloadInProgress)
        // we are downloading a remote tvguide.xml, wait for it to complete, before trying to read it (avoiding file locks)
      {
        // check if the download has been going on for too long, then flag it as failed.
        TimeSpan ts = now - _remoteFileDonwloadInProgressAt;
        if (ts.TotalSeconds > remoteFileDonwloadTimeoutSecs)
        {
          //timed out;
          _remoteFileDownloadInProgress = false;          
          Setting setting;
          SettingsManagement.SaveValue("xmlTvRemoteScheduleTransferStatus", "File transfer timed out.");
          SettingsManagement.SaveValue("xmlTvRemoteScheduleLastTransfer", now);

          this.LogInfo("File transfer timed out.");
        }
        else
        {
          this.LogInfo("File transfer is in progress. Waiting...");
          return;
        }
      }
      else
      {
        CheckNewTVGuide();
      }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    protected void RetrieveRemoteTvGuide()
    {
      //System.Diagnostics.Debugger.Launch();      
      if (_remoteFileDownloadInProgress)
      {
        return;
      }      

      bool remoteSchedulerEnabled = SettingsManagement.GetValue("xmlTvRemoteSchedulerEnabled", false);
      if (!remoteSchedulerEnabled)
      {
        _remoteFileDownloadInProgress = false;
        return;
      }

      DateTime now = DateTime.Now;

      DateTime defaultRemoteScheduleTime = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, 6, 30, 0);
      DateTime remoteScheduleTime = SettingsManagement.GetValue("xmlTvRemoteScheduleTime", defaultRemoteScheduleTime);

      DateTime lastTransfer = remoteScheduleTime.AddDays(-1);
      TimeSpan tsSchedule = now - remoteScheduleTime;
      TimeSpan tsLastTransfer = DateTime.Now - lastTransfer;

      // if just recently resumed (max 5mins) and scheduled time hasn't exceeded the 5mins mark, then ok.
      //1440 mins = 1 day. - we only want to update once per day.
      if (tsLastTransfer.TotalMinutes > 1440 && tsSchedule.TotalMinutes < 5)
      {
        string folder = SettingsManagement.GetValue("xmlTv", DefaultOutputFolder);
        string URL = SettingsManagement.GetValue("xmlTvRemoteURL", "");
        RetrieveRemoteFile(folder, URL);
      }
      else
      {
        this.LogInfo("Not the time to fetch remote file yet");
      }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    protected void CheckNewTVGuide()
    {
      FileStream streamIn = null;
      StreamReader fileIn = null;      
      string folder = SettingsManagement.GetValue("xmlTv", DefaultOutputFolder);
      DateTime lastTime = SettingsManagement.GetValue("xmlTvLastUpdate", DateTime.MinValue);

      if (lastTime == DateTime.MinValue)
        this.LogInfo("xmlTvLastUpdate not found, forcing import");

      bool importXML = SettingsManagement.GetValue("xmlTvImportXML", true);
      bool importLST = SettingsManagement.GetValue("xmlTvImportLST", true);
      DateTime importDate = DateTime.MinValue; // gets the date of the newest file

      string fileName = folder + @"\tvguide.xml";

      if (importXML && System.IO.File.Exists(fileName))
      {
        DateTime fileTime = System.IO.File.GetLastWriteTime(fileName);
        if (importDate < fileTime)
        {
          importDate = fileTime;
        }
      }

      fileName = folder + @"\tvguide.lst";

      if (importLST && System.IO.File.Exists(fileName))
        // check if any files contained in tvguide.lst are newer than time of last import
      {
        try
        {
          DateTime fileTime = System.IO.File.GetLastWriteTime(fileName);
          if (importDate < fileTime)
          {
            importDate = fileTime;
          } // A new tvguide.lst should give an import, to retain compatibility with previous version
          Encoding fileEncoding = Encoding.Default;
          streamIn = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
          fileIn = new StreamReader(streamIn, fileEncoding, true);
          while (!fileIn.EndOfStream)
          {
            string tvguideFileName = fileIn.ReadLine();
            if (tvguideFileName.Length == 0) continue;
            if (!System.IO.Path.IsPathRooted(tvguideFileName))
            {
              // extend by directory
              tvguideFileName = System.IO.Path.Combine(folder, tvguideFileName);
            }
            if (System.IO.File.Exists(tvguideFileName))
            {
              DateTime tvfileTime = System.IO.File.GetLastWriteTime(tvguideFileName);

              if (tvfileTime > lastTime)
              {
                if (importDate < tvfileTime)
                {
                  importDate = tvfileTime;
                }
              }
            }
          }
        }
        finally
        {
          if (streamIn != null)
          {
            streamIn.Close();
            streamIn.Dispose();
          }
          if (fileIn != null)
          {
            fileIn.Close();
            fileIn.Dispose();
          }
        }
      }
      if ((importXML || importLST) && (DateTime.Parse(importDate.ToString()) > lastTime))
        // To string and back to avoid rounding errors leading to continous reimports!!!
      {
        StartImport(importXML, importLST, importDate);
      }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    protected void StartImport(bool importXML, bool importLST, DateTime importDate)
    {
      FileStream streamIn = null;
      StreamReader fileIn = null;
      if (_workerThreadRunning)
        return;
      
      string folder = SettingsManagement.GetValue("xmlTv", DefaultOutputFolder);

      Thread.Sleep(500); // give time to the external prog to close file handle

      if (importXML)
      {
        string fileName = folder + @"\tvguide.xml";

        try
        {
          //check if file can be opened for reading....
          IOUtil.CheckFileAccessRights(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception e)
        {
          this.LogError(@"plugin:xmltv StartImport - File [" + fileName + "] doesn't have read access : " + e.Message);
          return;
        }
      }

      if (importLST)
      {
        string fileName = folder + @"\tvguide.lst";

        try
        {
          IOUtil.CheckFileAccessRights(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception e)
        {
          this.LogError(@"plugin:xmltv StartImport - File [" + fileName + "] doesn't have read access : " + e.Message);
          return;
        }
        try //Check that all listed files can be read before starting import (and deleting programs list)
        {
          Encoding fileEncoding = Encoding.Default;
          streamIn = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
          fileIn = new StreamReader(streamIn, fileEncoding, true);
          while (!fileIn.EndOfStream)
          {
            string tvguideFileName = fileIn.ReadLine();
            if (tvguideFileName.Length == 0) continue;

            if (!System.IO.Path.IsPathRooted(tvguideFileName))
            {
              // extend by directory
              tvguideFileName = System.IO.Path.Combine(folder, tvguideFileName);
            }
            try
            {
              IOUtil.CheckFileAccessRights(tvguideFileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception e)
            {
              this.LogError(@"plugin:xmltv StartImport - File [" + tvguideFileName + "] doesn't have read access : " +
                        e.Message);
              return;
            }
          }
        }
        finally
        {
          if (streamIn != null)
          {
            streamIn.Close();
            streamIn.Dispose();
          }
          if (fileIn != null)
          {
            fileIn.Close();
            fileIn.Dispose();
          }
        }
      }

      _workerThreadRunning = true;
      ThreadParams param = new ThreadParams();
      param._importXML = importXML;
      param._importLST = importLST;
      param._importDate = importDate;
      Thread workerThread = new Thread(new ParameterizedThreadStart(ThreadFunctionImportTVGuide));
      workerThread.Name = "XmlTvImporter";
      workerThread.IsBackground = true;
      workerThread.Priority = ThreadPriority.Lowest;
      workerThread.Start(param);
    }


    private class ThreadParams
    {
      public bool _importXML;
      public bool _importLST;
      public DateTime _importDate;
    } ;

    private void ThreadFunctionImportTVGuide(object aparam)
    {
      SetStandbyAllowed(false);
      //System.Diagnostics.Debugger.Launch();      
      FileStream streamIn = null;
      StreamReader fileIn = null;

      try
      {
        if (GlobalServiceProvider.Instance.IsRegistered<IPowerScheduler>() &&
            GlobalServiceProvider.Instance.Get<IPowerScheduler>().IsSuspendInProgress())
        {
          return;
        }
        var param = (ThreadParams)aparam;

        string folder = SettingsManagement.GetValue("xmlTv", DefaultOutputFolder);

        // Allow for deleting of all existing programs before adding the new ones. 
        // Already imported programs might have incorrect data depending on the grabber & setup
        // f.e when grabbing programs many days ahead
        bool deleteBeforeImport = SettingsManagement.GetValue("xmlTvDeleteBeforeImport", true);        
        int numChannels = 0, numPrograms = 0;
        string errors = "";

        try
        {
          if (param._importXML)
          {
            string fileName = folder + @"\tvguide.xml";
            this.LogDebug("plugin:xmltv importing " + fileName);

            XMLTVImport import = new XMLTVImport(10); // add 10 msec delay to the background thread
            import.Import(fileName, deleteBeforeImport, false);

            numChannels += import.ImportStats.Channels;
            numPrograms += import.ImportStats.Programs;

            if (import.ErrorMessage.Length != 0)
              errors += "tvguide.xml:" + import.ErrorMessage + "; ";
          }

          if (param._importLST)
          {
            string fileName = folder + @"\tvguide.lst";
            this.LogDebug("plugin:xmltv importing files in " + fileName);

            Encoding fileEncoding = Encoding.Default;
            streamIn = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            fileIn = new StreamReader(streamIn, fileEncoding, true);

            while (!fileIn.EndOfStream)
            {
              string tvguideFileName = fileIn.ReadLine();
              if (tvguideFileName.Length == 0) continue;

              if (!System.IO.Path.IsPathRooted(tvguideFileName))
              {
                // extend by directory
                tvguideFileName = System.IO.Path.Combine(folder, tvguideFileName);
              }

              this.LogDebug(@"plugin:xmltv importing " + tvguideFileName);

              XMLTVImport import = new XMLTVImport(10); // add 10 msec dely to the background thread

              import.Import(tvguideFileName, deleteBeforeImport, false);

              numChannels += import.ImportStats.Channels;
              numPrograms += import.ImportStats.Programs;

              if (import.ErrorMessage.Length != 0)
                errors += tvguideFileName + ": " + import.ErrorMessage + "; ";
            }
          }

          SettingsManagement.SaveValue("xmlTvResultLastImport", DateTime.Now);
          SettingsManagement.SaveValue("xmlTvResultChannels", numChannels);
          SettingsManagement.SaveValue("xmlTvResultPrograms", numPrograms);
          SettingsManagement.SaveValue("xmlTvResultStatus", errors);
          this.LogDebug("Xmltv: imported {0} channels, {1} programs status:{2}", numChannels, numPrograms, errors);
        }
        catch (Exception ex)
        {
          this.LogError(ex, @"plugin:xmltv import failed");
        }

        SettingsManagement.SaveValue("xmlTvLastUpdate", param._importDate);
        this.LogInfo("Xmltv: waiting for database to finish inserting imported programs.");        
        ProgramManagement.InitiateInsertPrograms();
      }
      finally
      {
        this.LogDebug(@"plugin:xmltv import done");
        if (streamIn != null)
        {
          streamIn.Close();
          streamIn.Dispose();
        }
        if (fileIn != null)
        {
          fileIn.Close();
          fileIn.Dispose();
        }
        _workerThreadRunning = false;
        SetStandbyAllowed(true);
      }
    }

    private void EPGScheduleDue()
    {
      CheckNewTVGuide();
    }

    private void RegisterForEPGSchedule()
    {
      // Register with the EPGScheduleDue event so we are informed when
      // the EPG wakeup schedule is due.
      if (GlobalServiceProvider.Instance.IsRegistered<IEpgHandler>())
      {
        IEpgHandler handler = GlobalServiceProvider.Instance.Get<IEpgHandler>();
        if (handler != null)
        {
          handler.EPGScheduleDue += new EPGScheduleHandler(EPGScheduleDue);
          this.LogDebug("XmlTvImporter: registered with PowerScheduler EPG handler");
          return;
        }
      }
      this.LogDebug("XmlTvImporter: NOT registered with PowerScheduler EPG handler");
    }


    private void SetStandbyAllowed(bool allowed)
    {
      if (GlobalServiceProvider.Instance.IsRegistered<IEpgHandler>())
      {
        this.LogDebug("plugin:xmltv: Telling PowerScheduler standby is allowed: {0}, timeout is one hour", allowed);
        GlobalServiceProvider.Instance.Get<IEpgHandler>().SetStandbyAllowed(this, allowed, 3600);
      }
    }

    #endregion

    #region ITvServerPluginStartedAll Members

    public void StartedAll()
    {
      RegisterForEPGSchedule();
    }

    #endregion

    public object GetServiceInstance
    {
      get { return new XMLTVImportService(); }
    }

    public Type GetServiceInterfaceForContractType
    {
      get { return typeof(IXMLTVImportService); }
    }
  }
}