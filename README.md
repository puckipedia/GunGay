# GunGay

A replacement Sun Ray server. Provides you with a small library that allows you to do authentication **and** talk to the framebuffer.

## Why the name?
I asked on IRC and this is the first thing someone came up with.

## What does it do?
It allows you to talk to Sun Ray devices (at least the 2), without running stuff like Oracle Linux or Solaris.
It does:

 - Framebuffer
 - Smartcard talking

It doesn't do:

 - Keyboard/mouse events (it should, but there's something Not Working)
 - Audio
 - USB passthrough
 - Any kind of acceleration
 - Convincing the Sun Ray that it is actually connected (though it's being controlled)

## Setup:
In your DHCP server, set `option x-display-manager` to the IP of your server. The vendor class is `SUNW.NewT.SUNW`.
Then, write your code. Turn on the Sun Ray, and see it connect!