﻿using Facepunch.Steamworks;
using Sirenix.OdinInspector;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using System.Linq;
using System;

public class NetworkManager:SerializedMonoBehaviour {
    public static NetworkManager instance;

    public int networkSimulationRate = 20; //# of packets to send per second (at 60fps)
    private float networkSimulationTimer; //should this be frame based? maybe? idk for now just do it based on a timer
    private float _networkSimulationTimer; //should happen on fixedUpdate at least
    public float keepAliveTimer = 10f; //seconds

    public SteamConnection me;
    public Dictionary<ulong, SteamConnection> connections = new Dictionary<ulong, SteamConnection>();
    public int connectionCounter = 0;

    public List<string> MessageCodes = new List<string>();
    public List<SerializerAction> SerializeActions = new List<SerializerAction>();
    public List<DeserializerAction> DeserializeActions = new List<DeserializerAction>();
    public List<ProcessDeserializedDataAction> ProcessDeserializedDataActions = new List<ProcessDeserializedDataAction>();

    public delegate byte[] SerializerAction(int msgType, params object[] args);
    public delegate void DeserializerAction(ulong sender, int msgType, byte[] data);
    public delegate void ProcessDeserializedDataAction(ulong sender, params object[] args);

    void Awake() {
        DontDestroyOnLoad(this.gameObject);
        instance = this;

        networkSimulationTimer = (1f / 60f) * (60f / networkSimulationRate);
        //register internal message types
        RegisterInternalMessages.Register(); //to tidy it up, moved all this stuff to a nother class
    }

    #region Message Definition Helpers
    //Pass in null for serialize and deserialize if you have no data and just want to send a message id (for connect, keep alive, etc)
    public int RegisterMessageType(string messageName, SerializerAction serialize, DeserializerAction deserialize, ProcessDeserializedDataAction process) {
        MessageCodes.Add(messageName);
        SerializeActions.Add(serialize);
        DeserializeActions.Add(deserialize);
        ProcessDeserializedDataActions.Add(process);
        int messageId = MessageCodes.Count - 1;
        Debug.Log("Registered Message Type: " + messageName + " - id: " + messageId);
        return messageId;
    }

    public void Process(ulong sender, int msgType, params object[] args) {
        NetworkManager.instance.ProcessDeserializedDataActions[msgType](sender, args);
    }

    public byte[] Serialize(int msgId, params object[] args) {
        return SerializeActions[msgId](msgId, args);
    }

    public void Deserialize(ulong sender, int msgId, byte[] data) {
        DeserializeActions[msgId](sender, msgId, data);
    }
    #endregion

    public int GetMessageCode(string messageName) {
        if(MessageCodes.Contains(messageName)) {
            return MessageCodes.IndexOf(messageName);
        }
        throw new Exception("Message with name [" + messageName + "] does not exist");
        return -1;
    }

    //queue message to go out in the next packet (will be priority filtering eventually)
    //this is sent every 200ms, or once the queue reacheds MTU, or can be forced when you send a reliable message
    public void QueueMessage(ulong sendTo, string msgCode, params object[] args) {
        int iMsgCode = GetMessageCode(msgCode);
        QueueMessage(sendTo, iMsgCode, args);
    }

    public void QueueMessage(ulong sendTo, int msgCode, params object[] args) {
        Debug.Log("[SEND] " + MessageCodes[msgCode]);
        byte[] data = PackMessage(msgCode, args);
        SendP2PData(sendTo, data, data.Length, Networking.SendType.ReliableWithBuffering);        
        //SendP2PData(sendTo, data, data.Length);
    }

    /// <summary>
    /// shouldn't use this except for messages we want to send immediately (like connection requests/accepts or keep alives)
    /// Or when we want to force sending any buffered messages
    /// use QueueMessage instead
    /// </summary>
    public void SendMessage(ulong sendTo, int msgCode, params object[] args) {
        Debug.Log("[SEND]  " + MessageCodes[msgCode]);
        byte[] data = PackMessage(msgCode, args);
        SendP2PData(sendTo, data, data.Length, Networking.SendType.Reliable);
    }

    public void SendMessage(ulong sendTo, string msgCode, params object[] args) {
        int iMsgCode = GetMessageCode(msgCode);
        SendMessage(sendTo, iMsgCode, args);
    }

    /// <summary>
    /// Combines msgCode and serialized message data (from args) into a byte[]
    /// </summary>
    public byte[] PackMessage(int msgCode, params object[] args) {
        if(msgCode > 255 || msgCode < 0) throw new Exception(string.Format("msgCode [{0}] is outside the accepted range of [0-255]", msgCode));
        byte[] data = new byte[1] { ((byte)msgCode) };
        if(SerializeActions[msgCode] != null) { //if we just want to send an "empty" message there is no serializer/deserializer
            byte[] msgData = Serialize(msgCode, args);
            data = data.Append(msgData);
        }
        return data;
    }

    //wrapper
    public bool SendP2PData(ulong steamID, byte[] data, int length, Networking.SendType sendType = Networking.SendType.Reliable, int channel = 0) {
        if(connections.ContainsKey(steamID)) {
            connections[steamID].timeSinceLastMsg = 0f;
        } //otherwise we just haven't established the connection yet (as this must be a connect request message)
        return Client.Instance.Networking.SendP2PPacket(steamID, data, data.Length, sendType, channel);
    }

    //callback from SteamClient. Read the data and decide how to process it.
    public void ReceiveP2PData(ulong steamID, byte[] bytes, int length, int channel) {
        //[00000000][0000....0000] ...
        //byte[0] => number of messages packed into this packet
        //byte[0] => message code
        //byte[1->n] => data for the message

        byte[] msgCodeBytes = bytes.Take(1).ToArray();

        int msgCode = msgCodeBytes[0];
        Debug.Log("[REC] " + MessageCodes[msgCode]);

        byte[] msgData = null;
        if(DeserializeActions[msgCode] != null) {
           msgData = bytes.Skip(1).ToArray();
            NetworkManager.instance.Deserialize(steamID, msgCode, msgData);
        } else {
            NetworkManager.instance.Process(steamID, msgCode); //usually called in Deserialize, but since we have no data just forward the messageCode
        }
    }

    //connection stuff below
    public void RegisterMyConnection(ulong steamID) {
        SteamConnection c = new SteamConnection();
        c.steamID = steamID;
        c.connectionIndex = 0;
        me = c;
    }

    public void RegisterConnection(ulong steamID, int playerNum = -1) {
        if(connections.ContainsKey(steamID)) return; //already in the list
        SteamConnection c = new SteamConnection();
        c.steamID = steamID;
        c.connectionIndex = playerNum;
        c.connectionEstablishedTime = Time.realtimeSinceStartup;
        connections.Add(c.steamID, c);
    }

    public void RemoveConnection(ulong steamID) {
        if(connections.ContainsKey(steamID)) {
            connections.Remove(steamID);
        }
    }

    //Highest Auth is the player with the lowest connectionIndex.  The host will have connectionIndex of 0
    //using an index instead of a bool here so I can have multiple authorities for seperated areas in the same game.
    //So that you find connected players in your area (map/region) and whoever has the highest auth sends state updates
    //to everyone else in the area. Similar to Destiny 2's physics hosts.
    public bool IsHighestAuth(SteamConnection sc) {
        int a = sc.connectionIndex;
        return connections.Any(s => s.Value.connectionIndex < a);
    }

    //This forces a disconnect with another player.
    //if they time out or leave or steam no longer detects an active connection state with them.
    public void Disconnect(ulong steamID) {
        RemoveConnection(steamID);
        Client.Instance.Networking.CloseSession(steamID);
        //cleanup stuff this player might have left behind, all player client owned networked objects instantiated on other clients?
    }

    //Connections fail for a few reasons, connection issues, steamID doesn't have the game running, etc...
    public void ConnectionFailed(ulong steamID, Networking.SessionError error) {
        Debug.Log("Connection Error: " + steamID + " - " + error);
    }

    //this is triggered when the first packet is sent to a connection.  The receiver is asked to accept or reject
    //the connection before receiving any messaages from them.  If we return false, we reject.
    //could add a check here to only accept a connection if we're in the same lobby (once we get lobby stuff working)
    public bool ConnectRequestResponse(ulong steamID) {
        Debug.Log("Incoming P2P Connection: " + steamID);
        return true;
    }

    //Update just handles checking the session state of all current connections, and if anyone has timed out/disconnected
    //remove them from the connection list and do a bit of cleaup
    public void FixedUpdate() {
     
        _networkSimulationTimer -= Time.fixedDeltaTime;
        foreach(var kvp in connections) {
            kvp.Value.timeSinceLastMsg += Time.fixedDeltaTime;
        }
        //network loop
        if(_networkSimulationTimer <= 0f) {
            _networkSimulationTimer = networkSimulationTimer;
            List<ulong> disconnects = new List<ulong>();

            foreach(var kvp in connections) {
                SteamConnection c = kvp.Value;

                if(me.HasAuthOver(c)) {//only send keepalives if you're the responsible one in this relationship
                    if(c.timeSinceLastMsg >= keepAliveTimer) { //15 seconds?
                        c.Ping();
                    }
                }

                Facepunch.Steamworks.Client.Instance.Networking.GetSessionState(c.steamID, out c.connectionState);

                if(c.connectionState.ConnectionActive == 0 && c.connectionState.Connecting == 0) {
                    disconnects.Add(c.steamID);
                }
            }

            for(int i = 0; i < disconnects.Count; i++) {
                Disconnect(disconnects[i]);
            }
        }
    }


    //cleanup method.  Closes all sessions when we close the game, this makes it so 
    //other players don't have to wait for a timeout to be detected before removing you when you leave (in most cases)
    public void CloseConnectionsOnDestroy() {
        if(Client.Instance == null) return;
        foreach(var sc in connections) {
            Client.Instance.Networking.CloseSession(sc.Value.steamID);
        }
    }

    private void OnDestroy() {
        CloseConnectionsOnDestroy();
        NetworkManager.instance = null;
    }

    
}
