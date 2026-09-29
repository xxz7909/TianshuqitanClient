These server frames come from the local Windows `runloop-demo.sqlite` recording.
They contain only public task, map and NPC data (no login, account, role or heartbeat data).

- 214: SC 003E, random ring 2, talk to 冯奇 at 十字路口 (17,73).
- 240 / 241 / 243: SC 0055 / 003C / 0016 map arrival and walkability data.
- 248: SC 001D, 冯奇's actual function menu.
- 270: SC 003E, next random ring 3, talk to 舞修罗 at 皇城内 (30,100).

`bounty-510.hex` is the task-only SC 003E frame 510 from the local Windows
`combat-example.sqlite` recording: task 1984, bounty 1, target NPC 90005 at
map 15 (12,57), giver 8011 at map 13 (30,100). It tests resuming an existing
bounty after reconnection and contains no account/session data.

`GameSessionIntegrationTest.recordedNpcTurnInRunsThroughTheTcpSession` replays them
through local entrance/game sockets. Its credentials and confirmation token are synthetic.
Passing this test does not verify the live game server or a physical Android device.
