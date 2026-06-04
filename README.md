


### What is this

A program to find an MRE (minimal reproducible example) for a bug

Currently the only supported mode is testing for a client crash when joining the headless, but hopefully I've written most of this code reusable enough for other cases where I need MRE for resonite issue, whenever I might need it again

### Public folder with related items

resrec:///U-1j4841f40i8/R-A7AC9249A2BB74B8D47B3B2AABE4383B8D0D81C8E9AD52D854580D77FBA13010
- IBS session initer: Spawned by the IBS in the headless to invite tester. Sends session ID to IBS.
- IBS auto-joiner: Listens for forwarded session IDs from IBS and immediately forces tester to join them.
- IBS auto-joiner as world: Just a grid world with auto-joiner already spawned. You need to resave this to your account and make it your home world, so every resonite restart loads this world.


