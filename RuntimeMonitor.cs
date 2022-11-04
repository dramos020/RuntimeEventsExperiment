﻿using System.Diagnostics.Tracing;
using Microsoft.Extensions.Logging;


class DiagnosticActivityInfo
{
    public string eventName { get;}
    public string id { get;}
    public DateTime? stopTime { get; set; }
    public DiagnosticActivityInfo(string name, string activityID)
    {
        eventName = name;
        id = activityID;
        stopTime = DateTime.MaxValue;
    }
}

class RuntimeEventListener : EventListener
{
    private ILogger m_logger;
    private static object s_consoleLock = new object();
    private Dictionary<string, DiagnosticActivityInfo> activityMappings = new Dictionary<string, DiagnosticActivityInfo>();
    private int runtimeEventCount = 0;
    private const int flushingTrigger = 100;

    public RuntimeEventListener(ILogger logger)
    {
        m_logger = logger;
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
        {
            // Keywords 0x1 are for GC events and 0x10 are for JIT events
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)(0x1 | 0x10));
        }
        else if (eventSource.Name == "Microsoft-Diagnostics-DiagnosticSource")
        {
            // DiagnosticSourceEventSource has this domain specific language to tell it what
            // you want to get events for. '[AS]*' tells it to give you all System.Diagnostic.Activity
            // events.
            Dictionary<string, string?> args = new Dictionary<string, string?>();
            args["FilterAndPayloadSpecs"] = "[AS]*";
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x2, args);
        }
        else if (eventSource.Name == "System.Threading.Tasks.TplEventSource") 
        {
            // Activity IDs aren't enabled by default.
            // Enabling Keyword 0x80 on the TplEventSource turns them on
            EnableEvents(eventSource, EventLevel.Informational, (EventKeywords)0x80);
        }
    }

    // This makes a mapping of runtime events to the System.Diagnostic.Activity that was active
    // when they were emitted. This is done by mapping the event's EventWrittenArgs.ActivityID
    // to a System.Diagnostics.Activity.ID. 
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        String eventDataActivityID = eventData.ActivityId.ToString();
        //Console.WriteLine($"{eventData.EventName}: {eventData.ActivityId} {eventData.TimeStamp.Ticks} {eventData.OSThreadId}");
        lock (s_consoleLock)
        {
            if (eventData.EventName == "ActivityStart")
            {
                object[] args = (object[])eventData.Payload![2]!;
                string activityID = GetIDFromActivityPayload(args);
                activityMappings.Add(eventDataActivityID, new DiagnosticActivityInfo(eventData.EventName, activityID));
            }
            else if (eventData.EventName == "ActivityStop")
            {
                activityMappings[eventDataActivityID].stopTime = eventData.TimeStamp;
            }
            else if (eventData.EventSource.Name == "Microsoft-Windows-DotNETRuntime")
            {
                if (activityMappings.ContainsKey(eventDataActivityID))
                {
                    m_logger.LogInformation($"Runtime event {eventData.EventName} is associated with activity {activityMappings[eventDataActivityID].id}");
                } 
                else
                {
                    m_logger.LogInformation($"Could not find activity to associate to runtime event {eventData.EventName}.");
                }

                if (++runtimeEventCount % flushingTrigger == 0)
                {
                    FlushMappings(eventData.TimeStamp);
                }
            }
        }
    }

    private void FlushMappings(DateTime timestamp)
    {
        List<string> activitiesToRemove = new List<string>();

        foreach (KeyValuePair<string, DiagnosticActivityInfo> mapping in activityMappings)
        {
            if(mapping.Value.stopTime < timestamp){
                activitiesToRemove.Add(mapping.Key);
            }
        }
        
        foreach (string activityID in activitiesToRemove)
        {
            activityMappings.Remove(activityID);
        }
    }

    private static string GetIDFromActivityPayload(object[] arguments)
    {
        foreach (object obj in arguments)
        {
            IDictionary<string, object> arg = (IDictionary<string, object>)obj;
            if (arg["Key"].Equals("Id"))
            {
                return (string)arg["Value"];
            }
        }
        throw new ApplicationException("Activity.ID was not found.");
    }
}