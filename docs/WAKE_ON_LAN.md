# Wake-on-LAN

Select a saved PC on the Connection page, open **Wake settings**, enter its
network adapter MAC address, and save. **Wake** remains available while that PC
is offline. Remembered PCs remain listed after discovery and application restarts.
Recent agents provide their active adapter MAC address automatically after
connection or an administrator's version check; manual settings take precedence.

**Wake computer** broadcasts from this computer's active IPv4 Ethernet/Wi-Fi
networks. Leave the destination blank for local broadcasts, or enter a specific
IPv4 broadcast/router hostname and UDP port (default 9). This UDP port is separate
from Remote Debugger's TCP connection port. A router must be configured to deliver
the packets if the destination is on another network.

Previously saved helper configurations continue to use the selected helper until
their Wake settings are saved again. Saving now selects this PC as the sender.

The target's firmware and network adapter must support and enable Wake-on-LAN.
Sleep/hibernate/shutdown behavior depends on the hardware and Windows settings;
packet transmission alone does not prove that a PC started. The app reports
**Wake packets sent** and leaves the PC offline until discovery sees it.
See Microsoft's [Windows WOL behavior](https://learn.microsoft.com/en-us/troubleshoot/windows-client/setup-upgrade-and-drivers/wake-on-lan-feature)
and [wake packet routing guidance](https://learn.microsoft.com/en-us/intune/configmgr/core/clients/deploy/plan/plan-wake-up-clients).

Settings are stored per device certificate identity in the current Windows user's
DPAPI-protected `device-wake-settings.dpapi`. Sending is manual; there is no
automatic wake on connect, update, discovery, or startup.
