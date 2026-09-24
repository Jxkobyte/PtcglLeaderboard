-- PTCGL Leaderboard & Match History - starts Pokemon TCG Live with the leaderboard.
--
-- The installer compiles this into ~/Applications/PTCGL Leaderboard.app with osacompile. All the
-- work is done by launch.sh, in ~/Library/Application Support/PtcglLeaderboard/mac; this only
-- asks about updates and shows what launch.sh reports. Kept to plain ASCII on purpose.

on run
	set launcherPath to (POSIX path of (path to home folder)) & "Library/Application Support/PtcglLeaderboard/mac/launch.sh"
	set launcher to "/bin/bash " & quoted form of launcherPath

	try
		do shell script "/bin/test -f " & quoted form of launcherPath
	on error
		display alert "PTCGL Leaderboard" message "The leaderboard's files are missing. Please run the installer again." as critical
		return
	end try

	set newerVersion to ""
	try
		set newerVersion to do shell script launcher & " check-update"
	end try
	if newerVersion is not "" then
		set reply to display dialog "A new version of PTCGL Leaderboard & Match History is available: " & newerVersion & "." & return & return & "Download it now? The game won't start, so you can install the new version straight away." & return & return & "Choose Play Now to play - you'll be reminded tomorrow." buttons {"Play Now", "Download"} default button "Download" with title "PTCGL Leaderboard" with icon note
		if button returned of reply is "Download" then
			open location "https://github.com/Jxkobyte/ptcgl-leaderboard/releases/latest"
			return
		end if
	end if

	try
		do shell script launcher & " launch"
	on error errorText
		display alert "PTCGL Leaderboard" message errorText as critical
	end try
end run
