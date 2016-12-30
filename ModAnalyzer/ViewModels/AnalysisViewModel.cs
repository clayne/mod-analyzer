﻿using GalaSoft.MvvmLight;
using GalaSoft.MvvmLight.CommandWpf;
using ModAnalyzer.Domain;
using ModAnalyzer.Messages;
using ModAssetMapper;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;

namespace ModAnalyzer.ViewModels {
    public class AnalysisViewModel : ViewModelBase {
        private ModAnalyzerService _modAnalyzerService;

        public ICommand ResetCommand { get; set; }
        public ICommand ViewOutputCommand { get; set; }
        public bool CanReset { get; set; }

        private string _log;

        public string Log {
            get { return _log; }
            set { Set(nameof(Log), ref _log, value); }
        }

        private string _progressMessage;

        public string ProgressMessage {
            get { return _progressMessage; }
            set { Set(nameof(ProgressMessage), ref _progressMessage, value); }
        }

        public AnalysisViewModel() {
            CanReset = false;

            _modAnalyzerService = new ModAnalyzerService();
            _modAnalyzerService.MessageReported += _modAnalyzerService_MessageReported;
            _modAnalyzerService.AnalysisCompleted += _modAnalyzerService_AnalysisComplete;

            ResetCommand = new RelayCommand(() => MessengerInstance.Send(new AnalysisCompleteMessage()));
            ViewOutputCommand = new RelayCommand(() => Process.Start("output"));

            MessengerInstance.Register<AnalyzeArchivesMessage>(this, OnAnalyzeArchives);
        }

        private void _modAnalyzerService_MessageReported(object sender, MessageReportedEventArgs e) {
            App.Current.Dispatcher.BeginInvoke((Action)(() => Log += e.Message + Environment.NewLine));

            if (e.IsStatusMessage) {
                App.Current.Dispatcher.BeginInvoke((Action)(() => ProgressMessage = e.Message.Trim()));
            }
        }

        private void _modAnalyzerService_AnalysisComplete(object sender, EventArgs e) {
            CanReset = true;
            RaisePropertyChanged("CanReset");
        }

        private void OnAnalyzeArchives(AnalyzeArchivesMessage message) {
            Log = string.Empty;
            _modAnalyzerService.AnalyzeMod(message.ModOptions);
        }
    }
}
