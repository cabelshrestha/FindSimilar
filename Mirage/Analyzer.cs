/*
 * The code is originally based on Mirage - High Performance Music Similarity Generator
 * http://hop.at/mirage
 *
 * Copyright (C) 2007-2008 Dominik Schnitzer <dominik@schnitzer.at>
 * Changed and heavily modified by Per Ivar Nerseth <perivar@nerseth.com>
 */

using System;
using System.Linq;
using System.Threading;
using System.Collections;
using System.Collections.Generic;
using System.IO;

using Comirva.Audio;
using Comirva.Audio.Extraction;
using Comirva.Audio.Feature;

using System.Globalization;

using CommonUtils;

// For drawing graph
using ZedGraph;
using System.Drawing;
using System.Drawing.Imaging;

using Wavelets;
using math.transform.jwave;
using math.transform.jwave.handlers;
using math.transform.jwave.handlers.wavelets;

using Soundfingerprinting;
using Soundfingerprinting.Audio.Services;
using Soundfingerprinting.Fingerprinting;
using Soundfingerprinting.Fingerprinting.FFT;
using Soundfingerprinting.Fingerprinting.Wavelets;
using Soundfingerprinting.Fingerprinting.Configuration;
using Soundfingerprinting.Fingerprinting.WorkUnitBuilder;
using Soundfingerprinting.Image;
using Soundfingerprinting.Audio.Models;
using Soundfingerprinting.Hashing;
using Soundfingerprinting.DbStorage;
using Soundfingerprinting.DbStorage.Entities;
using Soundfingerprinting.SoundTools;
using Soundfingerprinting.Audio.Strides;

namespace Mirage
{
	public class Analyzer
	{
		public const bool DEBUG_INFO_VERBOSE = false;
		public const bool DEFAULT_DEBUG_INFO = false;
		public const bool DEBUG_OUTPUT_TEXT = false;
		public const bool DEBUG_DO_INVERSE_TESTS = false;
		
		public enum AnalysisMethod {
			SCMS = 1,
			MandelEllis = 2,
			AudioFingerprinting = 3
		}
		
		// parameters: samplerate: 5512 hz, overlap: 31/32, window length: 2048
		// slice (window) size: 2048 / 5512 * 1000 =  371 ms
		// distance between slices: 64 / 5512 * 1000 =  11,6 ms

		// parameters: samplerate: 44100 hz, overlap: 1024 samples, window length: 2048
		// slice (window) size: 2048 / 44100 * 1000 =  46.44 ms
		// distance between slices: 1024 / 44100 * 1000 =  23.22 ms

		// parameters: samplerate: 32000 hz, overlap: 372 samples, window length: 11889
		// slice (window) size: 11889 / 32000 * 1000 =  371 ms
		// distance between slices: 372 / 32000 * 1000 =  11,6 ms

		public const int SAMPLING_RATE = 32000; 	// Using 32000 (instead of 44100) gives us a max of 16 khz resolution, which is OK for normal adult human hearing

		// 8192 / 32000 = 256 ms
		private const int WINDOW_SIZE = 8192; 		// 371 ms 	is	2048/5512	or 	16384/44100	or 11889/32000

		// Note! Due to the way we compute the mfcc we cannot use another overlap than half the window size
		// 4096 / 32000 = 128 ms
		private const int OVERLAP = WINDOW_SIZE/2;	// 11,6 ms	is 	64/5512		or	512/44100	or 372/32000
		private const int MEL_COEFFICIENTS = 40;	// Originally Mirage uses 36 filters but SPHINX-III uses 40
		public const int MFCC_COEFFICIENTS = 20; 	// 20 seems like a good number of mfcc coefficients
		public const int SECONDS_TO_ANALYZE = 60;
		
		// Explode samples to the range of 16 bit shorts (�32,768 to 32,767)
		// Matlab multiplies with 2^15 (32768)
		public const int AUDIO_MULTIPLIER = 65536; // 32768 still makes alot of mfcc feature computations fail!
		
		// The MfccMirage methods of calculating the filters only supports an overlap that is half the window size
		private static MfccMirage mfccMirage = new MfccMirage(WINDOW_SIZE, SAMPLING_RATE, MEL_COEFFICIENTS, MFCC_COEFFICIENTS);
		private static StftMirage stftMirage = new StftMirage(WINDOW_SIZE, OVERLAP, new HannWindow());

		// Create a static mandel ellis extractor
		private static MandelEllisExtractor mandelEllisExtractor = new MandelEllisExtractor(SAMPLING_RATE, WINDOW_SIZE, MFCC_COEFFICIENTS, MEL_COEFFICIENTS);
		
		// Soundfingerprinting static variables
		private static FingerprintService fingerprintService = GetSoundfingerprintingService();
		private static IFingerprintingConfiguration fingerprintingConfigCreation = new FullFrequencyFingerprintingConfiguration();
		private static IFingerprintingConfiguration fingerprintingConfigQuerying = new FullFrequencyFingerprintingConfiguration(true);
		private static IPermutations permutations = new LocalPermutations("Soundfingerprinting\\perms.csv", ",");

		public static AudioFeature AnalyzeMandelEllis(FileInfo filePath, bool doOutputDebugInfo=DEFAULT_DEBUG_INFO)
		{
			DbgTimer t = new DbgTimer();
			t.Start ();

			// get work config from the audio file
			WorkUnitParameterObject param = GetWorkUnitParameterObjectFromAudioFile(filePath);
			
			// Calculate the audio feature
			AudioFeature audioFeature = mandelEllisExtractor.Calculate(MathUtils.FloatToDouble(param.AudioSamples));
			
			if (audioFeature != null) {
				// Store duration
				audioFeature.Duration = (long) param.DurationInMs;
				
				// Store file name
				audioFeature.Name = filePath.Name;
			}
			
			Dbg.WriteLine ("MandelEllisExtractor - Total Execution Time: {0} ms", t.Stop().TotalMilliseconds);

			return audioFeature;
		}
		
		public static AudioFeature AnalyzeScms(FileInfo filePath, bool doOutputDebugInfo=DEFAULT_DEBUG_INFO, bool useHaarWavelet = true)
		{
			DbgTimer t = new DbgTimer();
			t.Start ();

			// get work config from the audio file
			WorkUnitParameterObject param = GetWorkUnitParameterObjectFromAudioFile(filePath, doOutputDebugInfo);
			string fileName = param.FileName;
			
			// used to save wave files in the debug inverse methods
			FindSimilar.AudioProxies.BassProxy bass = FindSimilar.AudioProxies.BassProxy.Instance;
			
			// 2. Windowing
			// 3. FFT
			Comirva.Audio.Util.Maths.Matrix stftdata = stftMirage.Apply(param.AudioSamples);

			if (DEBUG_INFO_VERBOSE & DEBUG_OUTPUT_TEXT) {
				stftdata.WriteAscii(fileName + "_stftdata.ascii");
				stftdata.WriteCSV(fileName + "_stftdata.csv", ";");
			}

			if (doOutputDebugInfo) {
				// same as specgram(audio*32768, 2048, 44100, hanning(2048), 1024);
				//stftdata.DrawMatrixImageLogValues(fileName + "_specgram.png", true);
				
				// spec gram with log values for the y axis (frequency)
				stftdata.DrawMatrixImageLogY(fileName + "_specgramlog.png", SAMPLING_RATE, 20, SAMPLING_RATE/2, 120, WINDOW_SIZE);
			}
			
			if (DEBUG_DO_INVERSE_TESTS) {
				#region Inverse STFT
				double[] audiodata_inverse_stft = stftMirage.InverseStft(stftdata);
				
				// divide
				//MathUtils.Divide(ref audiodata_inverse_stft, AUDIO_MULTIPLIER);
				MathUtils.Normalize(ref audiodata_inverse_stft);

				if (DEBUG_OUTPUT_TEXT) {
					WriteAscii(audiodata_inverse_stft, fileName + "_audiodata_inverse_stft.ascii");
					WriteF3Formatted(audiodata_inverse_stft, fileName + "_audiodata_inverse_stft.txt");
				}
				
				DrawGraph(audiodata_inverse_stft, fileName + "_audiodata_inverse_stft.png");
				
				float[] audiodata_inverse_float = MathUtils.DoubleToFloat(audiodata_inverse_stft);
				bass.SaveFile(audiodata_inverse_float, fileName + "_inverse_stft.wav", Analyzer.SAMPLING_RATE);
				#endregion
			}
			
			// 4. Mel Scale Filterbank
			// Mel-frequency is proportional to the logarithm of the linear frequency,
			// reflecting similar effects in the human's subjective aural perception)
			// 5. Take Logarithm
			// 6. DCT (Discrete Cosine Transform)

			if (DEBUG_INFO_VERBOSE) {
				#region Mel Scale and Log Values
				Comirva.Audio.Util.Maths.Matrix mellog = mfccMirage.ApplyMelScaleAndLog(ref stftdata);
				
				if (DEBUG_OUTPUT_TEXT) {
					mellog.WriteCSV(fileName + "_mel_log.csv", ";");
				}
				
				if (doOutputDebugInfo) {
					mellog.DrawMatrixImage(fileName + "_mel_log.png", 600, 400, true, true);
				}
				#endregion
				
				#region Inverse Mel Scale and Log Values
				if (DEBUG_DO_INVERSE_TESTS) {
					Comirva.Audio.Util.Maths.Matrix inverse_mellog = mfccMirage.InverseMelScaleAndLog(ref mellog);

					inverse_mellog.WriteCSV(fileName + "_mel_log_inverse.csv", ";");
					inverse_mellog.DrawMatrixImageLogValues(fileName + "_mel_log_inverse.png", true);
					
					double[] audiodata_inverse_mellog = stftMirage.InverseStft(inverse_mellog);
					//MathUtils.Divide(ref audiodata_inverse_mellog, AUDIO_MULTIPLIER/100);
					MathUtils.Normalize(ref audiodata_inverse_mellog);

					if (DEBUG_OUTPUT_TEXT) {
						WriteAscii(audiodata_inverse_mellog, fileName + "_audiodata_inverse_mellog.ascii");
						WriteF3Formatted(audiodata_inverse_mellog, fileName + "_audiodata_inverse_mellog.txt");
					}
					
					DrawGraph(audiodata_inverse_mellog, fileName + "_audiodata_inverse_mellog.png");
					
					float[] audiodata_inverse_mellog_float = MathUtils.DoubleToFloat(audiodata_inverse_mellog);
					bass.SaveFile(audiodata_inverse_mellog_float, fileName + "_inverse_mellog.wav", Analyzer.SAMPLING_RATE);
				}
				#endregion
			}

			Comirva.Audio.Util.Maths.Matrix featureData = null;
			if (useHaarWavelet) {
				#region Wavelet Transform
				int lastHeight = 0;
				int lastWidth = 0;
				featureData = mfccMirage.ApplyMelScaleAndWaveletCompress(ref stftdata, out lastHeight, out lastWidth);

				if (DEBUG_INFO_VERBOSE & DEBUG_OUTPUT_TEXT) {
					featureData.WriteAscii(fileName + "_waveletdata.ascii");
				}

				if (doOutputDebugInfo) {
					featureData.DrawMatrixImageLogValues(fileName + "_waveletdata.png", true);
				}
				
				if (DEBUG_DO_INVERSE_TESTS) {
					#region Inverse Wavelet
					// try to do an inverse wavelet transform
					Comirva.Audio.Util.Maths.Matrix stftdata_inverse_wavelet = mfccMirage.InverseMelScaleAndWaveletCompress(ref featureData, lastHeight, lastWidth);

					if (DEBUG_OUTPUT_TEXT) stftdata_inverse_wavelet.WriteCSV(fileName + "_specgramlog_inverse_wavelet.csv", ";");
					stftdata_inverse_wavelet.DrawMatrixImageLogValues(fileName + "_specgramlog_inverse_wavelet.png", true);
					
					double[] audiodata_inverse_wavelet = stftMirage.InverseStft(stftdata_inverse_wavelet);
					MathUtils.Normalize(ref audiodata_inverse_wavelet);
					
					if (DEBUG_OUTPUT_TEXT) WriteF3Formatted(audiodata_inverse_wavelet, fileName + "_audiodata_inverse_wavelet.txt");
					DrawGraph(audiodata_inverse_wavelet, fileName + "_audiodata_inverse_wavelet.png");
					bass.SaveFile(MathUtils.DoubleToFloat(audiodata_inverse_wavelet), fileName + "_inverse_wavelet.wav", Analyzer.SAMPLING_RATE);
					#endregion
				}
				#endregion
			} else {
				#region DCT Transform
				// It seems the Mirage way of applying the DCT is slightly faster than the
				// Comirva way due to less loops
				featureData = mfccMirage.ApplyMelScaleDCT(ref stftdata);
				//featureData = mfccMirage.ApplyComirvaWay(ref stftdata);

				if (DEBUG_INFO_VERBOSE & DEBUG_OUTPUT_TEXT) {
					featureData.WriteAscii(fileName + "_mfccdata.ascii");
				}

				if (doOutputDebugInfo) {
					featureData.DrawMatrixImageLogValues(fileName + "_mfccdata.png", true);
				}

				if (DEBUG_DO_INVERSE_TESTS) {
					#region Inverse MFCC
					// try to do an inverse mfcc
					Comirva.Audio.Util.Maths.Matrix stftdata_inverse_mfcc = mfccMirage.InverseMelScaleDCT(ref featureData);
					
					if (DEBUG_OUTPUT_TEXT) stftdata_inverse_mfcc.WriteCSV(fileName + "_stftdata_inverse_mfcc.csv", ";");
					stftdata_inverse_mfcc.DrawMatrixImageLogValues(fileName + "_specgramlog_inverse_mfcc.png", true);
					
					double[] audiodata_inverse_mfcc = stftMirage.InverseStft(stftdata_inverse_mfcc);
					MathUtils.Normalize(ref audiodata_inverse_mfcc);

					if (DEBUG_OUTPUT_TEXT) WriteF3Formatted(audiodata_inverse_mfcc, fileName + "_audiodata_inverse_mfcc.txt");
					DrawGraph(audiodata_inverse_mfcc, fileName + "_audiodata_inverse_mfcc.png");
					bass.SaveFile(MathUtils.DoubleToFloat(audiodata_inverse_mfcc), fileName + "_inverse_mfcc.wav", Analyzer.SAMPLING_RATE);
					#endregion
				}
				#endregion
			}
			
			// Store in a Statistical Cluster Model Similarity class.
			// A Gaussian representation of a song
			Scms audioFeature = Scms.GetScms(featureData, fileName);
			
			if (audioFeature != null) {
				
				// Store image if debugging
				if (doOutputDebugInfo) {
					audioFeature.Image = featureData.DrawMatrixImageLogValues(fileName + "_featuredata.png", true, false, 0, 0, true);
				}

				// Store bitstring hash as well
				string hashString = GetBitString(featureData);
				audioFeature.BitString = hashString;
				
				// Store duration
				audioFeature.Duration = (long) param.DurationInMs;
				
				// Store file name
				audioFeature.Name = filePath.FullName;
			} else {
				// failed creating the Scms class
				Console.Out.WriteLine("Failed! Could not compute the Scms {0}!", fileName);
			}
			
			Dbg.WriteLine ("AnalyzeScms - Total Execution Time: {0} ms", t.Stop().TotalMilliseconds);
			return audioFeature;
		}
		
		/// <summary>
		/// Return information from the Audio File
		/// </summary>
		/// <param name="filePath">filepath object</param>
		/// <returns>a WorkUnitParameter object</returns>
		public static WorkUnitParameterObject GetWorkUnitParameterObjectFromAudioFile(FileInfo filePath, bool doOutputDebugInfo=DEFAULT_DEBUG_INFO) {
			DbgTimer t = new DbgTimer();
			t.Start ();

			float[] audiodata = AudioFileReader.Decode(filePath.FullName, SAMPLING_RATE, SECONDS_TO_ANALYZE);
			if (audiodata == null || audiodata.Length == 0)  {
				Dbg.WriteLine("Error! - No Audio Found");
				return null;
			}
			
			// Name of file being processed
			string fileName = StringUtils.RemoveNonAsciiCharacters(Path.GetFileNameWithoutExtension(filePath.Name));
			
			#if DEBUG
			if (DEBUG_INFO_VERBOSE) {
				if (DEBUG_OUTPUT_TEXT) WriteAscii(audiodata, fileName + "_audiodata.ascii");
				if (DEBUG_OUTPUT_TEXT) WriteF3Formatted(audiodata, fileName + "_audiodata.txt");
			}
			#endif
			
			if (doOutputDebugInfo) {
				DrawGraph(MathUtils.FloatToDouble(audiodata), fileName + "_audiodata.png");
			}
			
			// Calculate duration in ms
			double duration = (double) audiodata.Length / SAMPLING_RATE * 1000;
			
			// Explode samples to the range of 16 bit shorts (�32,768 to 32,767)
			// Matlab multiplies with 2^15 (32768)
			// e.g. if( max(abs(speech))<=1 ), speech = speech * 2^15; end;
			MathUtils.Multiply(ref audiodata, AUDIO_MULTIPLIER);
			
			// zero pad if the audio file is too short to perform a mfcc
			if (audiodata.Length < (WINDOW_SIZE + OVERLAP))
			{
				int lenNew = WINDOW_SIZE + OVERLAP;
				Array.Resize<float>(ref audiodata, lenNew);
			}
			
			// work config
			WorkUnitParameterObject param = new WorkUnitParameterObject();
			param.AudioSamples = audiodata;
			param.PathToAudioFile = filePath.FullName;
			param.MillisecondsToProcess = SECONDS_TO_ANALYZE * 1000;
			param.StartAtMilliseconds = 0;
			param.FileName = fileName;
			param.DurationInMs = duration;
			param.Tags = GetTagInfoFromFile(filePath.FullName);

			Dbg.WriteLine ("Get Audio File Parameters - Execution Time: {0} ms", t.Stop().TotalMilliseconds);
			return param;
		}
		
		/// <summary>
		/// Method to analyze and add using the soundfingerprinting methods
		/// </summary>
		/// <param name="filePath">full file path</param>
		/// <param name="doOutputDebugInfo">decide whether to output debug info like spectrogram and audiofile (default value can be set)</param>
		/// <param name="useHaarWavelet">decide whether to use haar wavelet compression or DCT compression</param>
		/// <returns>true if successful</returns>
		public static bool AnalyzeAndAddSoundfingerprinting(FileInfo filePath, bool doOutputDebugInfo=DEFAULT_DEBUG_INFO, bool useHaarWavelet = true) {
			DbgTimer t = new DbgTimer();
			t.Start ();

			// get work config from the audio file
			WorkUnitParameterObject param = GetWorkUnitParameterObjectFromAudioFile(filePath);
			param.FingerprintingConfiguration = fingerprintingConfigCreation;
			string fileName = param.FileName;

			// build track
			Track track = new Track();
			track.Title = param.FileName;
			track.TrackLengthMs = (int) param.DurationInMs;
			track.FilePath = param.PathToAudioFile;
			track.Tags = param.Tags;
			track.Id = -1; // this will be set by the insert method
			
			// Get fingerprint signatures using the Soundfingerprinting methods
			DatabaseService databaseService = DatabaseService.Instance; // For AudioFingerprinting
			Repository repository = new Repository(permutations, databaseService, fingerprintService);

			double[][] logSpectrogram;
			if (repository.InsertTrackInDatabaseUsingSamples(track, 25, 4, param, out logSpectrogram)) {

				// store logSpectrogram as Matrix
				Comirva.Audio.Util.Maths.Matrix logSpectrogramMatrix = new Comirva.Audio.Util.Maths.Matrix(logSpectrogram);
				logSpectrogramMatrix = logSpectrogramMatrix.Transpose();
				
				#region Debug for Soundfingerprinting Method
				if (doOutputDebugInfo) {
					// Image Service
					ImageService imageService = new ImageService(fingerprintService.SpectrumService, fingerprintService.WaveletService);
					imageService.GetLogSpectralImages(logSpectrogram, fingerprintingConfigCreation.Stride, fingerprintingConfigCreation.FingerprintLength, fingerprintingConfigCreation.Overlap, 2).Save(fileName + "_specgram_logimages.png");
					
					logSpectrogramMatrix.DrawMatrixImageLogValues(fileName + "_specgram_logimage.png", true);
					
					if (DEBUG_OUTPUT_TEXT) {
						logSpectrogramMatrix.WriteCSV(fileName + "_specgram_log.csv", ";");
					}
				}
				#endregion
			} else {
				// failed
				Console.Out.WriteLine("Failed! Could not compute the soundfingerprint {0}!", fileName);
				return false;
			}

			Dbg.WriteLine ("AnalyzeAndAddSoundfingerprinting - Total Execution Time: {0} ms", t.Stop().TotalMilliseconds);
			return true;
		}

		/// <summary>
		/// Method to analyse and add all the different types of audio features
		/// </summary>
		/// <param name="filePath">full file path</param>
		/// <param name="db">Scms database (Mirage)</param>
		/// <param name="databaseService">soundfingerprinting database</param>
		/// <param name="doOutputDebugInfo">decide whether to output debug info like spectrogram and audiofile (default value can be set)</param>
		/// <param name="useHaarWavelet">decide whether to use haar wavelet compression or DCT compression</param>
		/// <returns>true if successful</returns>
		public static bool AnalyzeAndAddComplete(FileInfo filePath, Db db, DatabaseService databaseService, bool doOutputDebugInfo=DEFAULT_DEBUG_INFO, bool useHaarWavelet = true) {
			DbgTimer t = new DbgTimer();
			t.Start ();

			// get work config from the audio file
			WorkUnitParameterObject param = GetWorkUnitParameterObjectFromAudioFile(filePath);
			param.FingerprintingConfiguration = fingerprintingConfigCreation;
			string fileName = param.FileName;

			// build track
			Track track = new Track();
			track.Title = param.FileName;
			track.TrackLengthMs = (int) param.DurationInMs;
			track.FilePath = param.PathToAudioFile;
			track.Tags = param.Tags;
			track.Id = -1; // this will be set by the insert method
			
			// Get fingerprint signatures using the Soundfingerprinting methods
			Repository repository = new Repository(permutations, databaseService, fingerprintService);

			double[][] logSpectrogram;
			if (repository.InsertTrackInDatabaseUsingSamples(track, 25, 4, param, out logSpectrogram)) {

				// store logSpectrogram as Matrix
				Comirva.Audio.Util.Maths.Matrix logSpectrogramMatrix = new Comirva.Audio.Util.Maths.Matrix(logSpectrogram);
				logSpectrogramMatrix = logSpectrogramMatrix.Transpose();
				
				#region Debug for Soundfingerprinting Method
				if (doOutputDebugInfo) {
					// Image Service
					ImageService imageService = new ImageService(fingerprintService.SpectrumService, fingerprintService.WaveletService);
					imageService.GetLogSpectralImages(logSpectrogram, fingerprintingConfigCreation.Stride, fingerprintingConfigCreation.FingerprintLength, fingerprintingConfigCreation.Overlap, 2).Save(fileName + "_specgram_logimages.png");
					
					logSpectrogramMatrix.DrawMatrixImageLogValues(fileName + "_specgram_logimage.png", true);
					
					if (DEBUG_OUTPUT_TEXT) {
						logSpectrogramMatrix.WriteCSV(fileName + "_specgram_log.csv", ";");
					}
				}
				#endregion
				
				// Insert Statistical Cluster Model Similarity Audio Feature as well
				if (!AnalyseAndAddScmsUsingLogSpectrogram(logSpectrogramMatrix, param, db, track.Id, doOutputDebugInfo, useHaarWavelet)) {
					// failed, but ignore?
				}
			} else {
				// failed
				return false;
			}

			Dbg.WriteLine ("AnalyzeAndAddComplete - Total Execution Time: {0} ms", t.Stop().TotalMilliseconds);
			return true;
		}

		public static bool AnalyzeAndAddCompleteNew(FileInfo filePath, Db db, DatabaseService databaseService, bool doOutputDebugInfo=DEFAULT_DEBUG_INFO, bool useHaarWavelet = true) {
			DbgTimer t = new DbgTimer();
			t.Start ();

			// get work config from the audio file
			WorkUnitParameterObject param = GetWorkUnitParameterObjectFromAudioFile(filePath);
			string fileName = param.FileName;
			
			// used to save wave files in the debug inverse methods
			FindSimilar.AudioProxies.BassProxy bass = FindSimilar.AudioProxies.BassProxy.Instance;
			
			// 2. Windowing
			// 3. FFT
			Comirva.Audio.Util.Maths.Matrix stftdata = stftMirage.Apply(param.AudioSamples);

			if (DEBUG_INFO_VERBOSE & DEBUG_OUTPUT_TEXT) {
				stftdata.WriteAscii(fileName + "_stftdata.ascii");
				stftdata.WriteCSV(fileName + "_stftdata.csv", ";");
			}

			if (doOutputDebugInfo) {
				// same as specgram(audio*32768, 2048, 44100, hanning(2048), 1024);
				//stftdata.DrawMatrixImageLogValues(fileName + "_specgram.png", true);
				
				// spec gram with log values for the y axis (frequency)
				stftdata.DrawMatrixImageLogY(fileName + "_specgramlog.png", SAMPLING_RATE, 20, SAMPLING_RATE/2, 120, WINDOW_SIZE);
			}
			
			if (DEBUG_DO_INVERSE_TESTS) {
				#region Inverse STFT
				double[] audiodata_inverse_stft = stftMirage.InverseStft(stftdata);
				
				// divide
				//MathUtils.Divide(ref audiodata_inverse_stft, AUDIO_MULTIPLIER);
				MathUtils.Normalize(ref audiodata_inverse_stft);

				if (DEBUG_OUTPUT_TEXT) {
					WriteAscii(audiodata_inverse_stft, fileName + "_audiodata_inverse_stft.ascii");
					WriteF3Formatted(audiodata_inverse_stft, fileName + "_audiodata_inverse_stft.txt");
				}
				
				DrawGraph(audiodata_inverse_stft, fileName + "_audiodata_inverse_stft.png");
				
				float[] audiodata_inverse_float = MathUtils.DoubleToFloat(audiodata_inverse_stft);
				bass.SaveFile(audiodata_inverse_float, fileName + "_inverse_stft.wav", Analyzer.SAMPLING_RATE);
				#endregion
			}
			
			// 4. Mel Scale Filterbank
			// Mel-frequency is proportional to the logarithm of the linear frequency,
			// reflecting similar effects in the human's subjective aural perception)
			// 5. Take Logarithm
			// 6. DCT (Discrete Cosine Transform)

			#region Mel Scale and Log Values
			Comirva.Audio.Util.Maths.Matrix mellog = mfccMirage.ApplyMelScaleAndLog(ref stftdata);
			
			if (DEBUG_OUTPUT_TEXT) {
				mellog.WriteCSV(fileName + "_mel_log.csv", ";");
			}
			
			if (doOutputDebugInfo) {
				mellog.DrawMatrixImage(fileName + "_mel_log.png", 600, 400, true, true);
			}
			#endregion
			
			#region Inverse Mel Scale and Log Values
			if (DEBUG_DO_INVERSE_TESTS) {
				Comirva.Audio.Util.Maths.Matrix inverse_mellog = mfccMirage.InverseMelScaleAndLog(ref mellog);

				inverse_mellog.WriteCSV(fileName + "_mel_log_inverse.csv", ";");
				inverse_mellog.DrawMatrixImageLogValues(fileName + "_mel_log_inverse.png", true);
				
				double[] audiodata_inverse_mellog = stftMirage.InverseStft(inverse_mellog);
				//MathUtils.Divide(ref audiodata_inverse_mellog, AUDIO_MULTIPLIER/100);
				MathUtils.Normalize(ref audiodata_inverse_mellog);

				if (DEBUG_OUTPUT_TEXT) {
					WriteAscii(audiodata_inverse_mellog, fileName + "_audiodata_inverse_mellog.ascii");
					WriteF3Formatted(audiodata_inverse_mellog, fileName + "_audiodata_inverse_mellog.txt");
				}
				
				DrawGraph(audiodata_inverse_mellog, fileName + "_audiodata_inverse_mellog.png");
				
				float[] audiodata_inverse_mellog_float = MathUtils.DoubleToFloat(audiodata_inverse_mellog);
				bass.SaveFile(audiodata_inverse_mellog_float, fileName + "_inverse_mellog.wav", Analyzer.SAMPLING_RATE);
			}
			#endregion

			
			Dbg.WriteLine ("AnalyzeAndAddComplete2 - Total Execution Time: {0} ms", t.Stop().TotalMilliseconds);
			return true;
		}
		
		/// <summary>
		/// Method to analyse and add a Statistical Cluster Model Similarity class to the database
		/// </summary>
		/// <param name="filePath">full file path</param>
		/// <param name="db">Scms database (Mirage)</param>
		/// <param name="doOutputDebugInfo">decide whether to output debug info like spectrogram and audiofile (default value can be set)</param>
		/// <param name="useHaarWavelet">decide whether to use haar wavelet compression or DCT compression</param>
		/// <returns>true if successful</returns>
		public static bool AnalyzeAndAddScms(FileInfo filePath, Db db, bool doOutputDebugInfo=DEFAULT_DEBUG_INFO, bool useHaarWavelet = true) {
			DbgTimer t = new DbgTimer();
			t.Start ();

			// get work config from the audio file
			WorkUnitParameterObject param = GetWorkUnitParameterObjectFromAudioFile(filePath);
			string fileName = param.FileName;
			
			// used to save wave files in the debug inverse methods
			FindSimilar.AudioProxies.BassProxy bass = FindSimilar.AudioProxies.BassProxy.Instance;
			
			// 2. Windowing
			// 3. FFT
			Comirva.Audio.Util.Maths.Matrix stftdata = stftMirage.Apply(param.AudioSamples);

			if (DEBUG_INFO_VERBOSE & DEBUG_OUTPUT_TEXT) {
				stftdata.WriteAscii(fileName + "_stftdata.ascii");
				stftdata.WriteCSV(fileName + "_stftdata.csv", ";");
			}

			if (doOutputDebugInfo) {
				// same as specgram(audio*32768, 2048, 44100, hanning(2048), 1024);
				//stftdata.DrawMatrixImageLogValues(fileName + "_specgram.png", true);
				
				// spec gram with log values for the y axis (frequency)
				stftdata.DrawMatrixImageLogY(fileName + "_specgramlog.png", SAMPLING_RATE, 20, SAMPLING_RATE/2, 120, WINDOW_SIZE);
			}
			
			if (DEBUG_DO_INVERSE_TESTS) {
				#region Inverse STFT
				double[] audiodata_inverse_stft = stftMirage.InverseStft(stftdata);
				
				// divide
				//MathUtils.Divide(ref audiodata_inverse_stft, AUDIO_MULTIPLIER);
				MathUtils.Normalize(ref audiodata_inverse_stft);

				if (DEBUG_OUTPUT_TEXT) {
					WriteAscii(audiodata_inverse_stft, fileName + "_audiodata_inverse_stft.ascii");
					WriteF3Formatted(audiodata_inverse_stft, fileName + "_audiodata_inverse_stft.txt");
				}
				
				DrawGraph(audiodata_inverse_stft, fileName + "_audiodata_inverse_stft.png");
				
				float[] audiodata_inverse_float = MathUtils.DoubleToFloat(audiodata_inverse_stft);
				bass.SaveFile(audiodata_inverse_float, fileName + "_inverse_stft.wav", Analyzer.SAMPLING_RATE);
				#endregion
			}
			
			// 4. Mel Scale Filterbank
			// Mel-frequency is proportional to the logarithm of the linear frequency,
			// reflecting similar effects in the human's subjective aural perception)
			// 5. Take Logarithm
			// 6. DCT (Discrete Cosine Transform)

			if (DEBUG_INFO_VERBOSE) {
				#region Mel Scale and Log Values
				Comirva.Audio.Util.Maths.Matrix mellog = mfccMirage.ApplyMelScaleAndLog(ref stftdata);
				
				if (DEBUG_OUTPUT_TEXT) {
					mellog.WriteCSV(fileName + "_mel_log.csv", ";");
				}
				
				if (doOutputDebugInfo) {
					mellog.DrawMatrixImage(fileName + "_mel_log.png", 600, 400, true, true);
				}
				#endregion
				
				#region Inverse Mel Scale and Log Values
				if (DEBUG_DO_INVERSE_TESTS) {
					Comirva.Audio.Util.Maths.Matrix inverse_mellog = mfccMirage.InverseMelScaleAndLog(ref mellog);

					inverse_mellog.WriteCSV(fileName + "_mel_log_inverse.csv", ";");
					inverse_mellog.DrawMatrixImageLogValues(fileName + "_mel_log_inverse.png", true);
					
					double[] audiodata_inverse_mellog = stftMirage.InverseStft(inverse_mellog);
					//MathUtils.Divide(ref audiodata_inverse_mellog, AUDIO_MULTIPLIER/100);
					MathUtils.Normalize(ref audiodata_inverse_mellog);

					if (DEBUG_OUTPUT_TEXT) {
						WriteAscii(audiodata_inverse_mellog, fileName + "_audiodata_inverse_mellog.ascii");
						WriteF3Formatted(audiodata_inverse_mellog, fileName + "_audiodata_inverse_mellog.txt");
					}
					
					DrawGraph(audiodata_inverse_mellog, fileName + "_audiodata_inverse_mellog.png");
					
					float[] audiodata_inverse_mellog_float = MathUtils.DoubleToFloat(audiodata_inverse_mellog);
					bass.SaveFile(audiodata_inverse_mellog_float, fileName + "_inverse_mellog.wav", Analyzer.SAMPLING_RATE);
				}
				#endregion
			}

			Comirva.Audio.Util.Maths.Matrix featureData = null;
			if (useHaarWavelet) {
				#region Wavelet Transform
				int lastHeight = 0;
				int lastWidth = 0;
				featureData = mfccMirage.ApplyMelScaleAndWaveletCompress(ref stftdata, out lastHeight, out lastWidth);

				if (DEBUG_INFO_VERBOSE & DEBUG_OUTPUT_TEXT) {
					featureData.WriteAscii(fileName + "_waveletdata.ascii");
				}

				if (doOutputDebugInfo) {
					featureData.DrawMatrixImageLogValues(fileName + "_waveletdata.png", true);
				}
				
				if (DEBUG_DO_INVERSE_TESTS) {
					#region Inverse Wavelet
					// try to do an inverse wavelet transform
					Comirva.Audio.Util.Maths.Matrix stftdata_inverse_wavelet = mfccMirage.InverseMelScaleAndWaveletCompress(ref featureData, lastHeight, lastWidth);

					if (DEBUG_OUTPUT_TEXT) stftdata_inverse_wavelet.WriteCSV(fileName + "_specgramlog_inverse_wavelet.csv", ";");
					stftdata_inverse_wavelet.DrawMatrixImageLogValues(fileName + "_specgramlog_inverse_wavelet.png", true);
					
					double[] audiodata_inverse_wavelet = stftMirage.InverseStft(stftdata_inverse_wavelet);
					MathUtils.Normalize(ref audiodata_inverse_wavelet);
					
					if (DEBUG_OUTPUT_TEXT) WriteF3Formatted(audiodata_inverse_wavelet, fileName + "_audiodata_inverse_wavelet.txt");
					DrawGraph(audiodata_inverse_wavelet, fileName + "_audiodata_inverse_wavelet.png");
					bass.SaveFile(MathUtils.DoubleToFloat(audiodata_inverse_wavelet), fileName + "_inverse_wavelet.wav", Analyzer.SAMPLING_RATE);
					#endregion
				}
				#endregion
			} else {
				#region DCT Transform
				// It seems the Mirage way of applying the DCT is slightly faster than the
				// Comirva way due to less loops
				featureData = mfccMirage.ApplyMelScaleDCT(ref stftdata);

				if (DEBUG_INFO_VERBOSE & DEBUG_OUTPUT_TEXT) {
					featureData.WriteAscii(fileName + "_mfccdata.ascii");
				}

				if (doOutputDebugInfo) {
					featureData.DrawMatrixImageLogValues(fileName + "_mfccdata.png", true);
				}

				if (DEBUG_DO_INVERSE_TESTS) {
					#region Inverse MFCC
					// try to do an inverse mfcc
					Comirva.Audio.Util.Maths.Matrix stftdata_inverse_mfcc = mfccMirage.InverseMelScaleDCT(ref featureData);
					
					if (DEBUG_OUTPUT_TEXT) stftdata_inverse_mfcc.WriteCSV(fileName + "_stftdata_inverse_mfcc.csv", ";");
					stftdata_inverse_mfcc.DrawMatrixImageLogValues(fileName + "_specgramlog_inverse_mfcc.png", true);
					
					double[] audiodata_inverse_mfcc = stftMirage.InverseStft(stftdata_inverse_mfcc);
					MathUtils.Normalize(ref audiodata_inverse_mfcc);

					if (DEBUG_OUTPUT_TEXT) WriteF3Formatted(audiodata_inverse_mfcc, fileName + "_audiodata_inverse_mfcc.txt");
					DrawGraph(audiodata_inverse_mfcc, fileName + "_audiodata_inverse_mfcc.png");
					bass.SaveFile(MathUtils.DoubleToFloat(audiodata_inverse_mfcc), fileName + "_inverse_mfcc.wav", Analyzer.SAMPLING_RATE);
					#endregion
				}
				#endregion
			}
			
			// Store in a Statistical Cluster Model Similarity class.
			// A Gaussian representation of a song
			Scms audioFeature = Scms.GetScms(featureData, fileName);
			
			if (audioFeature != null) {
				
				// Store image if debugging
				if (doOutputDebugInfo) {
					audioFeature.Image = featureData.DrawMatrixImageLogValues(fileName + "_featuredata.png", true, false, 0, 0, true);
				}

				// Store bitstring hash as well
				audioFeature.BitString = GetBitString(featureData);
				
				// Store duration
				audioFeature.Duration = (long) param.DurationInMs;
				
				// Store file name
				audioFeature.Name = filePath.FullName;
				
				// Add to database
				if (db.AddTrack(audioFeature) == -1) {
					Console.Out.WriteLine("Failed! Could not add audioFeature to database {0}!", fileName);
					return false;
				}
			} else {
				// failed creating the Scms class
				Console.Out.WriteLine("Failed! Could not compute the Scms {0}!", fileName);
				return false;
			}
			
			Dbg.WriteLine ("AnalyzeAndAddScms - Total Execution Time: {0} ms", t.Stop().TotalMilliseconds);
			return true;
		}
		
		/// <summary>
		/// Add the log spectrogram matrix as a Statistical Cluster Model Similarity class to the database
		/// </summary>
		/// <param name="logSpectrogramMatrix">log spectrogram matrix</param>
		/// <param name="fileName">clean filename without extension</param>
		/// <param name="fullFilePath">full file path</param>
		/// <param name="duration">duration in ms</param>
		/// <param name="db">database</param>
		/// <param name="trackId">track id to insert</param>
		/// <param name="doOutputDebugInfo">decide whether to output debug info like spectrogram and audiofile (default value can be set)</param>
		/// <param name="useHaarWavelet">decide whether to use haar wavelet compression or DCT compression</param>
		/// <returns>true if successful</returns>
		private static bool AnalyseAndAddScmsUsingLogSpectrogram(Comirva.Audio.Util.Maths.Matrix logSpectrogramMatrix,
		                                                         WorkUnitParameterObject param,
		                                                         Db db,
		                                                         int trackId,
		                                                         bool doOutputDebugInfo=DEFAULT_DEBUG_INFO,
		                                                         bool useHaarWavelet = true) {
			
			// Insert Statistical Cluster Model Similarity Audio Feature
			string fileName = param.FileName;

			Comirva.Audio.Util.Maths.Matrix scmsMatrix = null;
			if (useHaarWavelet) {
				#region Wavelet Transform
				int lastHeight = 0;
				int lastWidth = 0;
				scmsMatrix = mfccMirage.ApplyWaveletCompression(ref logSpectrogramMatrix, out lastHeight, out lastWidth);

				#if DEBUG
				if (DEBUG_INFO_VERBOSE) {
					if (DEBUG_OUTPUT_TEXT) scmsMatrix.WriteAscii(fileName + "_waveletdata.ascii");
				}
				#endif

				if (doOutputDebugInfo) {
					scmsMatrix.DrawMatrixImageLogValues(fileName + "_waveletdata.png", true);
				}
				
				#if DEBUG
				if (DEBUG_DO_INVERSE_TESTS) {
					#region Inverse Wavelet
					// try to do an inverse wavelet transform
					Comirva.Audio.Util.Maths.Matrix stftdata_inverse_wavelet = mfccMirage.InverseWaveletCompression(ref scmsMatrix, lastHeight, lastWidth, logSpectrogramMatrix.Rows, logSpectrogramMatrix.Columns);

					if (DEBUG_OUTPUT_TEXT) stftdata_inverse_wavelet.WriteCSV(fileName + "_specgramlog_inverse_wavelet.csv", ";");
					stftdata_inverse_wavelet.DrawMatrixImageLogValues(fileName + "_specgramlog_inverse_wavelet.png", true);
					#endregion
				}
				#endif
				#endregion
			} else {
				#region DCT Transform
				// It seems the Mirage way of applying the DCT is slightly faster than the
				// Comirva way due to less loops
				scmsMatrix = mfccMirage.ApplyDCT(ref logSpectrogramMatrix);

				#if DEBUG
				if (DEBUG_INFO_VERBOSE) {
					if (DEBUG_OUTPUT_TEXT) scmsMatrix.WriteAscii(fileName + "_mfccdata.ascii");
				}
				#endif

				if (doOutputDebugInfo) {
					scmsMatrix.DrawMatrixImageLogValues(fileName + "_mfccdata.png", true);
				}

				#if DEBUG
				if (DEBUG_DO_INVERSE_TESTS) {
					#region Inverse MFCC
					// try to do an inverse mfcc
					Comirva.Audio.Util.Maths.Matrix stftdata_inverse_mfcc = mfccMirage.InverseDCT(ref scmsMatrix);
					
					if (DEBUG_OUTPUT_TEXT) stftdata_inverse_mfcc.WriteCSV(fileName + "_stftdata_inverse_mfcc.csv", ";");
					stftdata_inverse_mfcc.DrawMatrixImageLogValues(fileName + "_specgramlog_inverse_mfcc.png", true);
					#endregion
				}
				#endif
				#endregion
			}
			
			// Store in a Statistical Cluster Model Similarity class.
			// i.e. a Gaussian representation of a song
			Scms audioFeature = Scms.GetScms(scmsMatrix, fileName);
			
			if (audioFeature != null) {
				
				// Store image if debugging
				if (doOutputDebugInfo) {
					audioFeature.Image = scmsMatrix.DrawMatrixImageLogValues(fileName + "_featuredata.png", true, false, 0, 0, true);
				}

				// Store bitstring hash as well
				string hashString = GetBitString(scmsMatrix);
				audioFeature.BitString = hashString;
				
				// Store duration
				audioFeature.Duration = (long) param.DurationInMs;
				
				// Store file name
				audioFeature.Name = param.PathToAudioFile;
				
				// Add to database
				int id = trackId;
				if (db.AddTrack(ref id, audioFeature) == -1) {
					Console.Out.WriteLine("Failed! Could not add audio feature to database ({0})!", fileName);
					return false;
				} else {
					return true;
				}
			} else {
				Console.Out.WriteLine("Error! Could not compute the Scms for '{0}'!", fileName);
				return false;
			}
		}
		
		/// <summary>
		/// Query the database for perceptually similar tracks using the sound fingerprinting methods
		/// </summary>
		/// <param name="filePath">input file</param>
		/// <returns>a dictionary of similar tracks</returns>
		public static Dictionary<Track, double> SimilarTracksSoundfingerprinting(FileInfo filePath) {
			DbgTimer t = new DbgTimer();
			t.Start ();

			// get work config from the audio file
			WorkUnitParameterObject param = GetWorkUnitParameterObjectFromAudioFile(filePath);
			param.FingerprintingConfiguration = fingerprintingConfigQuerying;
			
			// Get database
			DatabaseService databaseService = DatabaseService.Instance;
			Repository repository = new Repository(permutations, databaseService, fingerprintService);

			Dictionary<Track, double> candidates = repository.FindSimilarFromAudioSamples(25, 4, 1, param);

			Dbg.WriteLine ("SimilarTracksSoundfingerprinting - Total Execution Time: {0} ms", t.Stop().TotalMilliseconds);
			return candidates;
		}
		
		public static List<FindSimilar.QueryResult> SimilarTracksSoundfingerprintingList(FileInfo filePath) {
			DbgTimer t = new DbgTimer();
			t.Start ();

			// get work config from the audio file
			WorkUnitParameterObject param = GetWorkUnitParameterObjectFromAudioFile(filePath);
			param.FingerprintingConfiguration = fingerprintingConfigQuerying;
			
			// Get database
			DatabaseService databaseService = DatabaseService.Instance;
			Repository repository = new Repository(permutations, databaseService, fingerprintService);

			// TODO: i don't really know how the threshold tables work.
			// 1 returns more similar hits
			// 2 returns sometimes only the one we search for
			// even 0 seem to work (like 1)
			List<FindSimilar.QueryResult> candidates = repository.FindSimilarFromAudioSamplesList(25, 4, 0, param);

			Dbg.WriteLine ("SimilarTracksSoundfingerprintingList - Total Execution Time: {0} ms", t.Stop().TotalMilliseconds);
			return candidates;
		}
		
		/// <summary>
		/// Read tags from file using the BASS plugin
		/// </summary>
		/// <param name="filePath">filepath to file</param>
		/// <returns>a dictionary with tag names and tag values</returns>
		private static Dictionary<string, string> GetTagInfoFromFile(string filePath) {
			
			// Read TAGs using BASS
			FindSimilar.AudioProxies.BassProxy bass = FindSimilar.AudioProxies.BassProxy.Instance;
			Un4seen.Bass.AddOn.Tags.TAG_INFO tag_info = bass.GetTagInfoFromFile(filePath);

			Dictionary<string, string> tags = new Dictionary<string, string>();
			if (tag_info != null) {
				//if (tag_info.title != string.Empty) tags.Add("title", CleanTagValue(tag_info.title));
				if (tag_info.artist != string.Empty) tags.Add("artist", CleanTagValue(tag_info.artist));
				if (tag_info.album != string.Empty) tags.Add("album", CleanTagValue(tag_info.album));
				if (tag_info.albumartist != string.Empty) tags.Add("albumartist", CleanTagValue(tag_info.albumartist));
				if (tag_info.year != string.Empty) tags.Add("year", CleanTagValue(tag_info.year));
				if (tag_info.comment != string.Empty) tags.Add("comment", CleanTagValue(tag_info.comment));
				if (tag_info.genre != string.Empty) tags.Add("genre", CleanTagValue(tag_info.genre));
				if (tag_info.track != string.Empty) tags.Add("track", CleanTagValue(tag_info.track));
				if (tag_info.disc != string.Empty) tags.Add("disc", CleanTagValue(tag_info.disc));
				if (tag_info.copyright != string.Empty) tags.Add("copyright", CleanTagValue(tag_info.copyright));
				if (tag_info.encodedby != string.Empty) tags.Add("encodedby", CleanTagValue(tag_info.encodedby));
				if (tag_info.composer != string.Empty) tags.Add("composer", CleanTagValue(tag_info.composer));
				if (tag_info.publisher != string.Empty) tags.Add("publisher", CleanTagValue(tag_info.publisher));
				if (tag_info.lyricist != string.Empty) tags.Add("lyricist", CleanTagValue(tag_info.lyricist));
				if (tag_info.remixer != string.Empty) tags.Add("remixer", CleanTagValue(tag_info.remixer));
				if (tag_info.producer != string.Empty) tags.Add("producer", CleanTagValue(tag_info.producer));
				if (tag_info.bpm != string.Empty) tags.Add("bpm", CleanTagValue(tag_info.bpm));
				//if (tag_info.filename != string.Empty) tags.Add("filename", CleanTagValue(tag_info.filename));
				tags.Add("channelinfo", tag_info.channelinfo.ToString());
				//if (tag_info.duration > 0) tags.Add("duration", tag_info.duration.ToString());
				if (tag_info.bitrate > 0) tags.Add("bitrate", tag_info.bitrate.ToString());
				if (tag_info.replaygain_track_gain != -100f) tags.Add("replaygain_track_gain", tag_info.replaygain_track_gain.ToString());
				if (tag_info.replaygain_track_peak != -1f) tags.Add("replaygain_track_peak", tag_info.replaygain_track_peak.ToString());
				if (tag_info.conductor != string.Empty) tags.Add("conductor", CleanTagValue(tag_info.conductor));
				if (tag_info.grouping != string.Empty) tags.Add("grouping", CleanTagValue(tag_info.grouping));
				if (tag_info.mood != string.Empty) tags.Add("mood", CleanTagValue(tag_info.mood));
				if (tag_info.rating != string.Empty) tags.Add("rating", CleanTagValue(tag_info.rating));
				if (tag_info.isrc != string.Empty) tags.Add("isrc", CleanTagValue(tag_info.isrc));
				
				foreach(var nativeTag in tag_info.NativeTags) {
					string[] keyvalue = nativeTag.Split('=');
					tags.Add(keyvalue[0], CleanTagValue(keyvalue[1]));
				}
			}
			return tags;
		}

		private static string CleanTagValue(string uncleanValue) {
			return StringUtils.RemoveInvalidCharacters(uncleanValue);
		}
		
		#region Utility Methods to draw graphs and output text or text files
		/// <summary>
		/// Graphs an array of doubles varying between -1 and 1
		/// </summary>
		/// <param name="data">data</param>
		/// <param name="fileName">filename to save png to</param>
		/// <param name="onlyCanvas">true if no borders should be printed</param>
		public static void DrawGraph(double[] data, string fileName, bool onlyCanvas=false)
		{
			GraphPane myPane = new GraphPane( new RectangleF( 0, 0, 1200, 600 ), "", "", "" );
			
			if (onlyCanvas) {
				myPane.Chart.Border.IsVisible = false;
				myPane.Chart.Fill.IsVisible = false;
				myPane.Fill.Color = Color.Black;
				myPane.Margin.All = 0;
				myPane.Title.IsVisible = false;
				myPane.XAxis.IsVisible = false;
				myPane.YAxis.IsVisible = false;
			}
			myPane.XAxis.Scale.Max = data.Length - 1;
			myPane.XAxis.Scale.Min = 0;
			//myPane.YAxis.Scale.Max = 1;
			//myPane.YAxis.Scale.Min = -1;
			
			// add pretty stuff
			myPane.Fill = new Fill( Color.WhiteSmoke, Color.Lavender, 0F );
			myPane.Chart.Fill = new Fill( Color.FromArgb( 255, 255, 245 ),
			                             Color.FromArgb( 255, 255, 190 ), 90F );
			
			var timeData = Enumerable.Range(0, data.Length)
				.Select(i => (double) i)
				.ToArray();
			myPane.AddCurve(null, timeData, data, Color.Blue, SymbolType.None);
			
			Bitmap bm = new Bitmap( 1, 1 );
			using ( Graphics g = Graphics.FromImage( bm ) )
				myPane.AxisChange( g );
			
			myPane.GetImage().Save(fileName, ImageFormat.Png);
		}
		
		/// <summary>Writes the float array to an ascii-textfile that can be read by Matlab.
		/// Usage in Matlab: load('filename', '-ascii');</summary>
		/// <param name="filename">the name of the ascii file to create, e.g. "C:\\temp\\data.ascii"</param>
		public static void WriteAscii(float[] data, string filename)
		{
			TextWriter pw = File.CreateText(filename);
			for(int i = 0; i < data.Length; i++)
			{
				pw.Write(" {0}\r", data[i].ToString("#.00000000e+000", CultureInfo.InvariantCulture));
			}
			pw.Close();
		}

		/// <summary>Writes the double array to an ascii-textfile that can be read by Matlab.
		/// Usage in Matlab: load('filename', '-ascii');</summary>
		/// <param name="filename">the name of the ascii file to create, e.g. "C:\\temp\\data.ascii"</param>
		public static void WriteAscii(double[] data, string filename)
		{
			TextWriter pw = File.CreateText(filename);
			for(int i = 0; i < data.Length; i++)
			{
				pw.Write(" {0}\r", data[i].ToString("#.00000000e+000", CultureInfo.InvariantCulture));
			}
			pw.Close();
		}
		
		/// <summary>
		/// Write matrix to file using F3 formatting
		/// </summary>
		/// <param name="filename">filename</param>
		public static void WriteF3Formatted(float[] data, string filename) {
			TextWriter pw = File.CreateText(filename);
			for(int i = 0; i < data.Length; i++)
			{
				pw.Write("{0}", data[i].ToString("F3", CultureInfo.InvariantCulture).PadLeft(10) + " ");
				pw.Write("\r");
			}
			pw.Close();
		}
		
		/// <summary>
		/// Write matrix to file using F3 formatting
		/// </summary>
		/// <param name="filename">filename</param>
		public static void WriteF3Formatted(double[] data, string filename) {
			TextWriter pw = File.CreateText(filename);
			for(int i = 0; i < data.Length; i++)
			{
				pw.Write("{0}", data[i].ToString("F3", CultureInfo.InvariantCulture).PadLeft(10) + " ");
				pw.Write("\r");
			}
			pw.Close();
		}
		#endregion
		
		/// <summary>
		/// Computes the perceptual hash of an audio file as a bitstring using the mfcc matrix
		/// </summary>
		/// <param name="mfcc">mfcc Matrix</param>
		/// <returns>Returns a 'binary string' (aka bitstring) (like. 001010111011100010) which is easy to do a hamming distance on.</returns>
		private static string GetBitString(Comirva.Audio.Util.Maths.Matrix mfcc) {

			int rows = mfcc.Rows;
			int columns = mfcc.Columns;
			
			// 5. Compute the average value.
			// Compute the mean DCT value (using only
			// the 8x8 DCT low-frequency values and excluding the first term
			// since the DC coefficient can be significantly different from
			// the other values and will throw off the average).
			double total = 0;
			for (int x = 0; x < rows; x++) {
				for (int y = 0; y < columns; y++) {
					total += mfcc.MatrixData[x][y];
				}
			}
			total -= mfcc.MatrixData[0][0];
			
			double avg = total / (double)((rows * columns) - 1);

			// 6. Further reduce the DCT.
			// This is the magic step. Set the 64 hash bits to 0 or 1
			// depending on whether each of the 64 DCT values is above or
			// below the average value. The result doesn't tell us the
			// actual low frequencies; it just tells us the very-rough
			// relative scale of the frequencies to the mean. The result
			// will not vary as long as the overall structure of the image
			// remains the same; this can survive gamma and color histogram
			// adjustments without a problem.
			string hash = "";
			for (int x = 0; x < rows; x++) {
				for (int y = 0; y < columns; y++) {
					if (x != 0 && y != 0) {
						hash += (mfcc.MatrixData[x][y] > avg ? "1" : "0");
					}
				}
			}
			return hash;
		}
		
		private static FingerprintService GetSoundfingerprintingService() {

			// Audio service
			IAudioService audioService = new AudioService();
			
			// Fingerprint Descriptor
			FingerprintDescriptor fingerprintDescriptor = new FingerprintDescriptor();
			
			// SpectrumService
			SpectrumService spectrumService = new SpectrumService();
			
			// Wavelet Service
			IWaveletDecomposition waveletDecomposition = new Soundfingerprinting.Fingerprinting.Wavelets.StandardHaarWaveletDecomposition();
			IWaveletService waveletService = new WaveletService(waveletDecomposition);

			// Fingerprint Service
			FingerprintService fingerprintService = new FingerprintService(audioService,
			                                                               fingerprintDescriptor,
			                                                               spectrumService,
			                                                               waveletService);
			
			return fingerprintService;
		}
		
		private static List<bool[]> GetFingerprintSignatures(FingerprintService fingerprintService, float[] samples, string name) {
			
			Mirage.DbgTimer t = new Mirage.DbgTimer();
			t.Start();
			
			// work config
			WorkUnitParameterObject param = new WorkUnitParameterObject();
			param.FingerprintingConfiguration = fingerprintingConfigCreation;
			
			// Get fingerprints
			double[][] LogSpectrogram;
			List<bool[]> fingerprints = fingerprintService.CreateFingerprintsFromAudioSamples(samples, param, out LogSpectrogram);

			#if DEBUG
			if (Analyzer.DEBUG_INFO_VERBOSE) {
				// Image Service
				ImageService imageService =
					new ImageService(fingerprintService.SpectrumService, fingerprintService.WaveletService);
				
				int width = param.FingerprintingConfiguration.FingerprintLength;
				int height = param.FingerprintingConfiguration.LogBins;
				imageService.GetImageForFingerprints(fingerprints, width, height, 2).Save(name + "_fingerprints.png");
			}
			#endif
			
			Mirage.Dbg.WriteLine("GetFingerprintSignatures Execution Time: " + t.Stop().TotalMilliseconds + " ms");
			return fingerprints;
		}
	}
}
