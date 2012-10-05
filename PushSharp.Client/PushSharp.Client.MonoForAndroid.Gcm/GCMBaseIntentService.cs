using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using Android.Util;

namespace GCMSharp.Client
{
  	[Android.Runtime.Preserve(AllMembers=true)]
	internal abstract class GCMBaseIntentService : IntentService
	{
		const string TAG = "GCMBaseIntentService";

		const string WAKELOCK_KEY = "GCM_LIB";
		static PowerManager.WakeLock sWakeLock;

		static object LOCK = new object();

		string[] mSenderIds;
		//int sCounter = 1;
		Random sRandom = new Random();

		const int MAX_BACKOFF_MS = 3600000; //1 hour

		string TOKEN = "";
		const string EXTRA_TOKEN = "token";

		protected GCMBaseIntentService() : base() {}

		public GCMBaseIntentService(string[] senderIds) : base("GCMIntentService")
		{
			mSenderIds = senderIds;
		}


		protected abstract void OnMessage(Context context, Intent intent);

		protected virtual void OnDeletedMessages(Context context, int total)
		{
		}

		protected virtual bool OnRecoverableError(Context context, string errorId)
		{
			return true;
		}

		protected abstract void OnError(Context context, string errorId);

		protected abstract void OnRegistered(Context context, string registrationId);

		protected abstract void OnUnRegistered(Context context, string registrationId);


		protected override void OnHandleIntent(Intent intent)
		{
			try
			{
				var context = this.ApplicationContext;
				var action = intent.Action;

				if (action.Equals(GCMConstants.INTENT_FROM_GCM_REGISTRATION_CALLBACK))
				{
					handleRegistration(context, intent);
				}
				else if (action.Equals(GCMConstants.INTENT_FROM_GCM_MESSAGE))
				{
					// checks for special messages
					var messageType = intent.GetStringExtra(GCMConstants.EXTRA_SPECIAL_MESSAGE);
					if (messageType != null)
					{
						if (messageType.Equals(GCMConstants.VALUE_DELETED_MESSAGES))
						{
							var sTotal = intent.GetStringExtra(GCMConstants.EXTRA_TOTAL_DELETED);
							if (!string.IsNullOrEmpty(sTotal))
							{
								int nTotal = 0;
								if (int.TryParse(sTotal, out nTotal))
								{
									Log.Verbose(TAG, "Received deleted messages notification: " + nTotal);
									OnDeletedMessages(context, nTotal);
								}
								else
									Log.Error(TAG, "GCM returned invalid number of deleted messages: " + sTotal);
							}
						}
						else
						{
							// application is not using the latest GCM library
							Log.Error(TAG, "Received unknown special message: " + messageType);
						}
					}
					else
					{
						OnMessage(context, intent);
					}
				}
				else if (action.Equals(GCMConstants.INTENT_FROM_GCM_LIBRARY_RETRY))
				{
					var token = intent.GetStringExtra(EXTRA_TOKEN);

					if (!string.IsNullOrEmpty(token) && !TOKEN.Equals(token))
					{
						// make sure intent was generated by this class, not by a
						// malicious app.
						Log.Error(TAG, "Received invalid token: " + token);
						return;
					}

					// retry last call
					if (GCMRegistrar.IsRegistered(context))
						GCMRegistrar.internalUnRegister(context);
					else
						GCMRegistrar.internalRegister(context, mSenderIds);
				}
			}
			finally
			{
				// Release the power lock, so phone can get back to sleep.
				// The lock is reference-counted by default, so multiple
				// messages are ok.

				// If OnMessage() needs to spawn a thread or do something else,
				// it should use its own lock.
				lock (LOCK)
				{
					//Sanity check for null as this is a public method
					if (sWakeLock != null)
					{
						Log.Verbose(TAG, "Releasing Wakelock");
						sWakeLock.Release();
					}
					else
					{
						//Should never happen during normal workflow
						Log.Error(TAG, "Wakelock reference is null");
					}
				}
			}
		}



		internal static void RunIntentInService(Context context, Intent intent, Type classType) 
		{
			lock (LOCK) 
			{
				if (sWakeLock == null) 
				{
					// This is called from BroadcastReceiver, there is no init.
					var pm = PowerManager.FromContext(context);
					sWakeLock = pm.NewWakeLock(WakeLockFlags.Partial, WAKELOCK_KEY);
				}
			}
			
			Log.Verbose(TAG, "Acquiring wakelock");
			sWakeLock.Acquire();
			//intent.SetClassName(context, className);
			intent.SetClass(context, classType);

			context.StartService(intent);
		}

		private void handleRegistration(Context context, Intent intent)
		{
			var registrationId = intent.GetStringExtra(GCMConstants.EXTRA_REGISTRATION_ID);
			var error = intent.GetStringExtra(GCMConstants.EXTRA_ERROR);
			var unregistered = intent.GetStringExtra(GCMConstants.EXTRA_UNREGISTERED);

			Log.Debug(TAG, "handleRegistration: registrationId = " + registrationId + ", error = " + error + ", unregistered = " + unregistered);

			// registration succeeded
			if (registrationId != null)
			{
				GCMRegistrar.ResetBackoff(context);
				GCMRegistrar.SetRegistrationId(context, registrationId);
				OnRegistered(context, registrationId);
				return;
			}

			// unregistration succeeded
			if (unregistered != null)
			{
				// Remember we are unregistered
				GCMRegistrar.ResetBackoff(context);
				var oldRegistrationId = GCMRegistrar.ClearRegistrationId(context);
				OnUnRegistered(context, oldRegistrationId);
				return;
			}

			// last operation (registration or unregistration) returned an error;
			Log.Debug(TAG, "Registration error: " + error);
			// Registration failed
			if (GCMConstants.ERROR_SERVICE_NOT_AVAILABLE.Equals(error))
			{
				var retry = OnRecoverableError(context, error);

				if (retry)
				{
					int backoffTimeMs = GCMRegistrar.GetBackoff(context);
					int nextAttempt = backoffTimeMs / 2 + sRandom.Next(backoffTimeMs);

					Log.Debug(TAG, "Scheduling registration retry, backoff = " + nextAttempt + " (" + backoffTimeMs + ")");

					var retryIntent = new Intent(GCMConstants.INTENT_FROM_GCM_LIBRARY_RETRY);
					retryIntent.PutExtra(EXTRA_TOKEN, TOKEN);

					var retryPendingIntent = PendingIntent.GetBroadcast(context, 0, retryIntent, PendingIntentFlags.OneShot);

					var am = AlarmManager.FromContext(context);
					am.Set(AlarmType.ElapsedRealtime, SystemClock.ElapsedRealtime() + nextAttempt, retryPendingIntent);

					// Next retry should wait longer.
					if (backoffTimeMs < MAX_BACKOFF_MS)
					{
						GCMRegistrar.SetBackoff(context, backoffTimeMs * 2);
					}
				}
				else
				{
					Log.Debug(TAG, "Not retrying failed operation");
				}
			}
			else
			{
				// Unrecoverable error, notify app
				OnError(context, error);
			}
		}

	}
}