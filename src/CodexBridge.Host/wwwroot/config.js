import { RemoteTransport } from "./remote-transport.js?v=2026090107";
import { createRemotePeerConnector } from "./remote-peer-connector.js?v=2026090107";

export async function createConfiguredTransport() {
  return {
    mode: "remote",
    transport: new RemoteTransport({ connectPeer: createRemotePeerConnector() }),
  };
}

