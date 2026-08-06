import CocoaMQTT
import Foundation
import MageRideShared

/// The CocoaMQTT socket — D2' §C: *"MQTT — HiveMQ Android (foreground svc) / **CocoaMQTT
/// (background task)**"*.
///
/// `:shared` owns everything about *what* goes on the wire: the topics, the QoS and retain flags,
/// the CBOR payload, the last will and the credential, all computed once into an ``IosMqttPlan``.
/// This class owns only the socket, and deliberately reaches for that plan rather than restating
/// any of it — a topic string spelled here would be the second copy of a contract that already has
/// one. Same rule as `apps/driver-android/.../location/MqttPositionTransport.kt`.
///
/// **The username is the vehicle id.** `emqx.conf` refuses the CONNECT unless the token's
/// `vehicleId` claim equals it and `acl.conf` writes every device rule under `veh/{username}/`, so
/// the username is the whole identity and a token minted for another vehicle authorises nothing.
///
/// **EMQX validates the JWT at CONNECT only.** A rotation therefore takes effect on the *next*
/// connection, which is why ``connect(plan:)`` is called again whenever the session token changes
/// rather than a token being pushed into a live session.
final class CocoaMqttTransport: NSObject, IosPositionTransport {

    /// Called when the broker accepts a CONNECT, so the pipeline can open its replay window.
    var onConnected: (() -> Void)?

    /// Raw bytes off `veh/{vehicleId}/cmd`. Decoded by `:shared`, never here.
    var onCommand: ((Data) -> Void)?

    private var client: CocoaMQTT5?
    private var plan: IosMqttPlan?
    private let lock = NSLock()
    private var connected = false

    // MARK: - IosPositionTransport

    var isConnected: Bool {
        lock.lock()
        defer { lock.unlock() }
        return connected && client?.connState == .connected
    }

    func publish(topic: String, payload: NSData, qos: Int32, retain: Bool) -> Bool {
        guard let client, isConnected else { return false }
        let message = CocoaMQTT5Message(
            topic: topic,
            payload: [UInt8](Data(referencing: payload)),
            qos: CocoaMQTTQoS(rawValue: UInt8(clamping: Int(qos))) ?? .qos1,
            retained: retain
        )
        // A negative id means the client refused it — the socket is gone between our check and this
        // call. Answering `false` puts the sample back on the backlog rather than losing it.
        return client.publish(message, properties: MqttPublishProperties()) >= 0
    }

    // MARK: - Session

    /// Opens a session for [plan]'s vehicle, replacing any that is already open.
    func connect(plan: IosMqttPlan) {
        disconnect()

        let client = CocoaMQTT5(clientID: plan.clientId, host: plan.host, port: UInt16(plan.port))
        client.username = plan.username
        client.password = plan.password
        client.keepAlive = UInt16(plan.keepAliveSeconds)
        client.enableSSL = plan.useTls
        // A PERSISTENT session (ADD §7.1): the broker holds QoS-1 messages for a device that drops
        // mid-tunnel, which is the whole reason ingest is MQTT rather than HTTP.
        client.cleanSession = plan.cleanStart

        let properties = MqttConnectProperties()
        properties.sessionExpiryInterval = UInt32(clamping: Int(plan.sessionExpirySeconds))
        client.connectProperties = properties

        let will = CocoaMQTT5Message(
            topic: plan.willTopic,
            payload: [UInt8](Data(referencing: plan.willPayload)),
            qos: CocoaMQTTQoS(rawValue: UInt8(clamping: Int(plan.willQos))) ?? .qos1,
            retained: plan.willRetain
        )
        client.willMessage = will
        client.delegate = self
        // CocoaMQTT's own reconnect is deliberately off: R-09 bounds how hard a city's worth of
        // handsets may retry after a broker restart, and `:shared`'s ReconnectBackoff is where that
        // schedule lives. Two retry loops would race and neither would be the one R-09 describes.
        client.autoReconnect = false

        self.plan = plan
        self.client = client
        _ = client.connect(timeout: TimeInterval(plan.connectTimeoutSeconds))
    }

    /// Closes the session cleanly, announcing `offline` first.
    ///
    /// A clean DISCONNECT means the broker does **not** publish the last will, so the explicit
    /// `status` publish is what tells trip-state-svc, dispatch-svc and fleet-health-svc that the
    /// vehicle went away on purpose (R-15, T-04). Without it a driver who goes offline deliberately
    /// looks identical to one whose phone died — except that nobody is told at all.
    func disconnect() {
        guard let client else { return }
        if let plan, isConnected {
            _ = publish(
                topic: plan.statusTopic,
                payload: plan.offlinePayload(),
                qos: plan.willQos,
                retain: plan.willRetain
            )
        }
        client.disconnect()
        lock.lock()
        connected = false
        lock.unlock()
        self.client = nil
        self.plan = nil
    }
}

extension CocoaMqttTransport: CocoaMQTT5Delegate {

    func mqtt5(_ mqtt5: CocoaMQTT5, didConnectAck ack: CocoaMQTTCONNACKReasonCode, connAckData: MqttDecodeConnAck?) {
        guard ack == .success else { return }
        lock.lock()
        connected = true
        lock.unlock()

        if let plan {
            // `online` on the same retained topic the will uses — a late subscriber then learns the
            // state without waiting for the next sample.
            _ = publish(
                topic: plan.statusTopic,
                payload: plan.onlinePayload(),
                qos: plan.willQos,
                retain: plan.willRetain
            )
            // The only topic a device subscribes to. A failed subscription costs cadence hints
            // (R-07), not the position stream; the next reconnect re-subscribes.
            mqtt5.subscribe(
                plan.commandTopic,
                qos: CocoaMQTTQoS(rawValue: UInt8(clamping: Int(plan.commandQos))) ?? .qos1
            )
        }
        onConnected?()
    }

    func mqtt5(_ mqtt5: CocoaMQTT5, didReceiveMessage message: CocoaMQTT5Message, id: UInt16, publishData: MqttDecodePublish?) {
        onCommand?(Data(message.payload))
    }

    func mqtt5DidDisconnect(_ mqtt5: CocoaMQTT5, withError err: Error?) {
        lock.lock()
        connected = false
        lock.unlock()
    }

    // The rest of the delegate is required by the protocol and carries nothing this app acts on.
    // Publish acknowledgement is not one of them: QoS-1 delivery is the client's own retry, and a
    // sample this app considers unsent is one the client refused outright — see `publish`.
    func mqtt5(_ mqtt5: CocoaMQTT5, didPublishMessage message: CocoaMQTT5Message, id: UInt16) {}
    func mqtt5(_ mqtt5: CocoaMQTT5, didPublishAck id: UInt16, pubAckData: MqttDecodePubAck?) {}
    func mqtt5(_ mqtt5: CocoaMQTT5, didPublishRec id: UInt16, pubRecData: MqttDecodePubRec?) {}
    func mqtt5(_ mqtt5: CocoaMQTT5, didPublishComplete id: UInt16, pubCompData: MqttDecodePubComp?) {}
    func mqtt5(_ mqtt5: CocoaMQTT5, didSubscribeTopics success: NSDictionary, failed: [String], subAckData: MqttDecodeSubAck?) {}
    func mqtt5(_ mqtt5: CocoaMQTT5, didUnsubscribeTopics topics: [String], unsubAckData: MqttDecodeUnsubAck?) {}
    func mqtt5(_ mqtt5: CocoaMQTT5, didReceiveDisconnectReasonCode reasonCode: CocoaMQTTDISCONNECTReasonCode) {}
    func mqtt5(_ mqtt5: CocoaMQTT5, didReceiveAuthReasonCode reasonCode: CocoaMQTTAUTHReasonCode) {}
    func mqtt5DidPing(_ mqtt5: CocoaMQTT5) {}
    func mqtt5DidReceivePong(_ mqtt5: CocoaMQTT5) {}
}
